using Azure.Core;
using Azure.Identity;
using HybridRetrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

if (args.Length == 0 || args[0] is "help" or "--help")
{
    Console.WriteLine("""
    Hybrid retrieval — .NET 11, Microsoft Foundry, Azure AI Search
      sources                       Download and verify official source documents; no Azure calls
      ingest                        Embed documents in Foundry and upload to Azure AI Search
      compare "your query"          Inspect four real search strategies against the same index
      evaluate [repetitions]        Produce per-query JSON and Markdown comparison reports

    Set HYBRID_Search__Endpoint and HYBRID_Foundry__Endpoint.
    Use Azure credentials (az login / managed identity), or HYBRID_SEARCH_API_KEY
    and HYBRID_FOUNDRY_API_KEY. Read README.md before ingesting.
    """);
    return 0;
}

try
{
    var builder = Host.CreateApplicationBuilder();
    builder.Configuration.Sources.Clear();
    builder.Configuration.SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Local.json"), optional: true)
        .AddEnvironmentVariables("HYBRID_");
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
    var settings = builder.Configuration.Get<Settings>()
        ?? throw new InvalidOperationException("Missing retrieval settings.");
    settings.Validate(cloud: args[0] != "sources");
    builder.Services.AddSingleton(settings);
    builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ExcludeInteractiveBrowserCredential = true
    }));
    builder.Services.AddHttpClient("azure", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(90);
        client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
    }).AddStandardResilienceHandler(options =>
    {
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(80);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(25);
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;
        options.Retry.UseJitter = true;
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
    });
    builder.Services.AddHttpClient("sources", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(45);
        client.MaxResponseContentBufferSize = 2_000_000;
    });
    builder.Services.AddSingleton(provider => new CloudHttp(
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("azure"), provider.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton(provider => new Corpus(
        provider.GetRequiredService<IHttpClientFactory>().CreateClient("sources"), settings));
    builder.Services.AddSingleton<FoundryEmbeddings>();
    builder.Services.AddSingleton<AzureSearch>();
    builder.Services.AddSingleton<Runner>();
    using var host = builder.Build();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var runner = host.Services.GetRequiredService<Runner>();
    switch (args[0])
    {
        case "sources" when args.Length == 1: await runner.SourcesAsync(cts.Token); break;
        case "ingest" when args.Length == 1: await runner.IngestAsync(cts.Token); break;
        case "compare" when args.Length == 2: await runner.CompareAsync(args[1], cts.Token); break;
        case "evaluate" when args.Length is 1 or 2:
            await runner.EvaluateAsync(args.Length == 2 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1, cts.Token);
            break;
        default: throw new ArgumentException("Unknown command or arguments. Run with --help.");
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 130; }
catch (AuthenticationFailedException) { Console.Error.WriteLine("Azure authentication failed. Run az login, configure managed identity, or set service API keys."); return 1; }
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException or
    System.Text.Json.JsonException or IOException or TimeoutException or FormatException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
