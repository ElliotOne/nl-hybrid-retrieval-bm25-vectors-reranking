using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;

namespace HybridRetrieval.Tests;

public sealed class RetrievalTests
{
    private static Settings ValidSettings() => new()
    {
        Search = new() { Endpoint = "https://example.search.windows.net", IndexName = "test-hybrid-docs" },
        Foundry = new() { Endpoint = "https://example.openai.azure.com", Dimensions = 3 }
    };
    private static SearchHit Hit(string id, string source) => new(id, source, source, "https://example.com", "passage", 1, null);

    [Fact]
    public void KeywordVectorHybridAndRerankedRequestsHaveDistinctServiceSemantics()
    {
        var settings = ValidSettings();
        var keyword = AzureSearch.BuildQuery(settings, "retry HTTP 429", RetrievalMode.Keyword, null);
        var vector = AzureSearch.BuildQuery(settings, "retry HTTP 429", RetrievalMode.Vector, [1, 2, 3]);
        var hybrid = AzureSearch.BuildQuery(settings, "retry HTTP 429", RetrievalMode.Hybrid, [1, 2, 3]);
        var semantic = AzureSearch.BuildQuery(settings, "retry HTTP 429", RetrievalMode.Semantic, [1, 2, 3]);
        Assert.Null(keyword["vectorQueries"]);
        Assert.Null(vector["search"]);
        Assert.NotNull(hybrid["search"]);
        Assert.NotNull(hybrid["vectorQueries"]);
        Assert.Null(hybrid["queryType"]);
        Assert.Equal("semantic", semantic["queryType"]!.GetValue<string>());
        Assert.Equal("fail", semantic["semanticErrorHandling"]!.GetValue<string>());
        foreach (var request in new[] { keyword, vector, hybrid, semantic })
            Assert.Equal(50, request["top"]!.GetValue<int>());
    }

    [Fact]
    public void CompleteSemanticRerankingDoesNotSendAPartialOnlyWaitLimit()
    {
        var semantic = AzureSearch.BuildQuery(ValidSettings(), "circuit breaker", RetrievalMode.Semantic, [1, 2, 3]);
        Assert.Equal("fail", semantic["semanticErrorHandling"]!.GetValue<string>());
        Assert.False(semantic.ContainsKey("semanticMaxWaitInMilliseconds"));
    }

    [Fact]
    public void MetricsCollapseOverlappingChunksAndUseGradedSourceRelevance()
    {
        var hits = new[] { Hit("a1", "a"), Hit("a2", "a"), Hit("b1", "b"), Hit("c1", "c") };
        var labels = new Dictionary<string, int> { ["b"] = 3, ["c"] = 1 };
        var score = Evaluation.Score(hits, labels, 2);
        Assert.Equal(.5, score.RecallAtK);
        Assert.Equal(.5, score.ReciprocalRank);
        var expected = (7 / Math.Log2(3)) / (7 + 1 / Math.Log2(3));
        Assert.Equal(expected, score.NdcgAtK, 10);
    }

    [Fact]
    public void EmptyAndPerfectRankingsProduceMeaningfulMetrics()
    {
        var labels = new Dictionary<string, int> { ["a"] = 3 };
        Assert.Equal(new Quality(0, 0, 0), Evaluation.Score([], labels, 5));
        Assert.Equal(new Quality(1, 1, 1), Evaluation.Score([Hit("a", "a")], labels, 5));
    }

    [Fact]
    public void EmbeddingsAreMappedByProviderIndexInsteadOfArrayOrder()
    {
        var response = JsonNode.Parse("""{"data":[{"index":1,"embedding":[0,1,0]},{"index":0,"embedding":[1,0,0]}]}""")!.AsObject();
        var actual = FoundryEmbeddings.Parse(response, 2, 3);
        Assert.Equal(new float[] { 1, 0, 0 }, actual[0]);
        Assert.Equal(new float[] { 0, 1, 0 }, actual[1]);
    }

    [Theory]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[0,0,0]}]}")]
    [InlineData("{\"data\":[{\"index\":0,\"embedding\":[1,0]}]}")]
    [InlineData("{\"data\":[{\"index\":2,\"embedding\":[1,0,0]}]}")]
    public void InvalidEmbeddingsCannotReachTheIndex(string json) =>
        Assert.Throws<InvalidDataException>(() => FoundryEmbeddings.Parse(JsonNode.Parse(json)!.AsObject(), 1, 3));

    [Fact]
    public void DuplicateEmbeddingIndicesAreRejected()
    {
        var response = JsonNode.Parse("""{"data":[{"index":0,"embedding":[1,0,0]},{"index":0,"embedding":[0,1,0]}]}""")!.AsObject();
        Assert.Throws<InvalidDataException>(() => FoundryEmbeddings.Parse(response, 2, 3));
    }

    [Fact]
    public void ChunkingIsStableOverlappingAndKeepsTheFinalParagraph()
    {
        var source = new SourceDefinition("docs", "Docs", "docs/a.md", "https://example.com", "hash");
        var text = new string('a', 600) + "\n\n" + new string('b', 600) + "\n\nFINAL-PARAGRAPH";
        var doc = new SourceDocument(source, text);
        var first = Corpus.Split(doc, 700, 100);
        Assert.Equal(first, Corpus.Split(doc, 700, 100));
        Assert.All(first, c => Assert.InRange(c.Content.Length, 1, 700));
        Assert.EndsWith("FINAL-PARAGRAPH", first[^1].Content);
        Assert.Contains(first[0].Content[^100..], first[1].Content);
    }

    [Fact]
    public void SourcesAreCleanedWithoutDroppingApiNames()
    {
        var cleaned = Corpus.Clean("---\ntitle: Metadata\n---\n# IHttpClientFactory\n[Reference](https://example.com)\n[!code-csharp[](snippets/a.cs)]\n<!-- internal -->");
        Assert.Contains("IHttpClientFactory", cleaned);
        Assert.Contains("Reference", cleaned);
        Assert.DoesNotContain("Metadata", cleaned);
        Assert.DoesNotContain("snippets", cleaned);
        Assert.DoesNotContain("internal", cleaned);
    }

    [Fact]
    public void EndpointValidationRejectsLocalAndProjectUrls()
    {
        var settings = ValidSettings();
        settings.Validate(true);
        settings.Foundry.Endpoint = "http://localhost:11434";
        Assert.Throws<ArgumentException>(() => settings.Validate(true));
        settings.Foundry.Endpoint = "https://example.services.ai.azure.com/api/projects/test";
        Assert.Throws<ArgumentException>(() => settings.Validate(true));
    }

    [Fact]
    public async Task CloudRequestsDoNotExposeProviderBodiesOrCredentialsInErrors()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "secret source content");
        using var http = new HttpClient(handler);
        var cloud = new CloudHttp(http, new TestCredential());
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => cloud.SendAsync(HttpMethod.Post,
            new Uri("https://example.search.windows.net/indexes/test/docs/search"), new JsonObject(),
            "HYBRID_TEST_UNSET_KEY", "https://search.azure.com/.default", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("secret source content", error.Message);
        Assert.Equal("Bearer", handler.AuthScheme);
    }

    [Fact]
    public async Task SemanticFailureCannotBeReportedAsSuccessfulReranking()
    {
        var body = """{"@search.semanticPartialResponseReason":"Transient","value":[]}""";
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.OK, body));
        var search = new AzureSearch(ValidSettings(), new CloudHttp(http, new TestCredential()));
        await Assert.ThrowsAsync<InvalidDataException>(() => search.SearchAsync("retry", RetrievalMode.Semantic, [1, 2, 3], CancellationToken.None));
    }

    [Fact]
    public async Task FailedSemanticRequestsExposeStructuredErrorsAndCorrelationIds()
    {
        var body = """{"error":{"code":"InvalidRequestParameter","message":"Check semantic configuration.","details":[{"code":"InvalidSemanticConfiguration","message":"Configuration was not found."}]},"unrelated":"private-content"}""";
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.BadRequest, body));
        var search = new AzureSearch(ValidSettings(), new CloudHttp(http, new TestCredential()));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => search.SearchAsync("private-query", RetrievalMode.Semantic, [1, 2, 3], CancellationToken.None));
        Assert.Contains("semantic reranking", error.Message);
        Assert.Contains("InvalidRequestParameter", error.Message);
        Assert.Contains("Configuration was not found.", error.Message);
        Assert.Contains("Azure request-id: azure-test-request", error.Message);
        Assert.Contains("Client request ID:", error.Message);
        Assert.DoesNotContain("private-content", error.Message);
        Assert.DoesNotContain("private-query", error.Message);
    }

    [Fact]
    public async Task DiagnosticsRedactCredentialsAndFlattenControlCharacters()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"code":"BadRequest","message":"bad key secret-key and token secret-token\nsecond line\u001b[31m"}}""")
        };
        var text = await AzureErrorDiagnostics.ReadAsync(response, ["secret-key", "secret-token"], CancellationToken.None);
        Assert.DoesNotContain("secret-key", text);
        Assert.DoesNotContain("secret-token", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.Contains("[REDACTED]", text);
        Assert.Contains("second line", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnstructuredAndOversizedErrorBodiesAreNotPrinted(bool oversized)
    {
        var body = oversized ? new string('x', 20_000) : "<html>private upstream page</html>";
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(body) };
        var text = await AzureErrorDiagnostics.ReadAsync(response, [], CancellationToken.None);
        Assert.Contains("omitted", text);
        Assert.DoesNotContain("private upstream page", text);
        Assert.True(text.Length < 300);
    }

    [Fact]
    public void QueryLabelsMustReferToRealCorpusSources()
    {
        var sources = new SourceManifest("dotnet/docs", "revision", "license", [new("a", "A", "docs/a.md", "url", "hash")]);
        var queries = new QueryManifest("v1", "seed", [new("q", "test", "question", new() { ["missing"] = 3 })]);
        Assert.Throws<InvalidDataException>(() => Evaluation.Validate(queries, sources));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        public string? AuthScheme { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthScheme = request.Headers.Authorization?.Scheme;
            var response = new HttpResponseMessage(status) { Content = new StringContent(content) };
            response.Headers.Add("request-id", "azure-test-request");
            return Task.FromResult(response);
        }
    }
    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
