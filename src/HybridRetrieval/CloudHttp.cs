using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace HybridRetrieval;

// Reuses #50's request-scoped auth and sanitized errors, and #46's explicit embedding dimensions.
public sealed class CloudHttp(HttpClient http, TokenCredential credential)
{
    public async Task<JsonObject> SendAsync(HttpMethod method, Uri uri, JsonNode? payload,
        string keyVariable, string scope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        var clientRequestId = Guid.NewGuid().ToString();
        request.Headers.Add("x-ms-client-request-id", clientRequestId);
        request.Headers.Add("return-client-request-id", "true");
        var key = Environment.GetEnvironmentVariable(keyVariable);
        string? bearerToken = null;
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("api-key", key);
        else
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct);
            bearerToken = token.Token;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var diagnostics = await AzureErrorDiagnostics.ReadAsync(response, [key, bearerToken], ct);
            var mode = payload?["queryType"]?.GetValue<string>() == "semantic" ? " (semantic reranking)" : "";
            throw new HttpRequestException(
                $"{method} {uri.Host}{uri.AbsolutePath}{mode} returned HTTP {(int)response.StatusCode}.\n" +
                $"Client request ID: {clientRequestId}\n{diagnostics}", null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var node = await System.Text.Json.JsonSerializer.DeserializeAsync<JsonObject>(stream, cancellationToken: ct);
        return node ?? throw new InvalidDataException("Azure returned an empty JSON response.");
    }
}
