using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HybridRetrieval;

public sealed class AzureSearch(Settings settings, CloudHttp cloud)
{
    private Uri Endpoint(string path) => new(settings.Search.Endpoint.TrimEnd('/') + "/" + path +
        "?api-version=" + Uri.EscapeDataString(settings.Search.ApiVersion));

    private Task<JsonObject> SendAsync(HttpMethod method, string path, JsonNode? payload, CancellationToken ct) =>
        cloud.SendAsync(method, Endpoint(path), payload, settings.Search.ApiKeyEnvironmentVariable,
            "https://search.azure.com/.default", ct);

    public string CorpusSignature(SourceManifest manifest) => "hybrid-retrieval-v1:" + Corpus.Hash(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest, settings.Ingestion, settings.Foundry.Endpoint,
            settings.Foundry.EmbeddingDeployment, settings.Foundry.Dimensions,
            settings.Search.SemanticConfiguration
        }, Corpus.Json));

    public async Task EnsureIndexAsync(SourceManifest manifest, CancellationToken ct)
    {
        var signature = CorpusSignature(manifest);
        try
        {
            var existing = await SendAsync(HttpMethod.Get, $"indexes/{settings.Search.IndexName}", null, ct);
            if (existing["description"]?.GetValue<string>() != signature)
                throw new InvalidOperationException("This index belongs to a different corpus/configuration. Choose a new IndexName.");
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        await SendAsync(HttpMethod.Post, "indexes", CreateSchema(settings, signature), ct);
    }

    public static JsonObject CreateSchema(Settings settings, string description)
    {
        var schema = JsonNode.Parse("""
        {
          "name": "", "description": "",
          "fields": [
            {"name":"id","type":"Edm.String","key":true,"filterable":true},
            {"name":"sourceId","type":"Edm.String","filterable":true},
            {"name":"title","type":"Edm.String","searchable":true,"analyzer":"en.microsoft"},
            {"name":"url","type":"Edm.String"},
            {"name":"ordinal","type":"Edm.Int32"},
            {"name":"content","type":"Edm.String","searchable":true,"analyzer":"en.microsoft"},
            {"name":"contentVector","type":"Collection(Edm.Single)","searchable":true,"retrievable":false,
             "stored":false,"dimensions":1536,"vectorSearchProfile":"dense-profile"}
          ],
          "similarity":{"@odata.type":"#Microsoft.Azure.Search.BM25Similarity","k1":1.2,"b":0.75},
          "vectorSearch":{
            "algorithms":[{"name":"dense-hnsw","kind":"hnsw","hnswParameters":{"metric":"cosine","m":4,"efConstruction":400,"efSearch":500}}],
            "profiles":[{"name":"dense-profile","algorithm":"dense-hnsw"}]
          },
          "semantic":{"defaultConfiguration":"docs-semantic","configurations":[{
            "name":"docs-semantic","prioritizedFields":{
              "titleField":{"fieldName":"title"},"prioritizedContentFields":[{"fieldName":"content"}]
            }
          }]}
        }
        """)!.AsObject();
        schema["name"] = settings.Search.IndexName;
        schema["description"] = description;
        schema["fields"]![6]!["dimensions"] = settings.Foundry.Dimensions;
        schema["semantic"]!["defaultConfiguration"] = settings.Search.SemanticConfiguration;
        schema["semantic"]!["configurations"]![0]!["name"] = settings.Search.SemanticConfiguration;
        return schema;
    }

    public async Task UploadAsync(Chunk[] chunks, float[][] vectors, CancellationToken ct)
    {
        if (chunks.Length != vectors.Length) throw new ArgumentException("One embedding is required for every chunk.");
        var items = chunks.Select((chunk, i) => (JsonNode)new JsonObject
        {
            ["@search.action"] = "mergeOrUpload", ["id"] = chunk.Id, ["sourceId"] = chunk.SourceId,
            ["title"] = chunk.Title, ["url"] = chunk.Url, ["ordinal"] = chunk.Ordinal,
            ["content"] = chunk.Content, ["contentVector"] = JsonSerializer.SerializeToNode(vectors[i])
        }).ToArray();
        var response = await SendAsync(HttpMethod.Post, $"indexes/{settings.Search.IndexName}/docs/index",
            new JsonObject { ["value"] = new JsonArray(items) }, ct);
        var results = response["value"]?.AsArray() ?? throw new InvalidDataException("Search omitted indexing results.");
        if (results.Count != chunks.Length || results.Any(x => x?["status"]?.GetValue<bool>() != true))
            throw new InvalidOperationException("Azure Search rejected one or more documents. Re-run ingestion to retry idempotent uploads.");
    }

    public async Task WaitForCountAsync(int expected, CancellationToken ct)
    {
        // Indexing is asynchronous. Query the index before allowing a comparison to start.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var result = await SendAsync(HttpMethod.Post, $"indexes/{settings.Search.IndexName}/docs/search",
                new JsonObject { ["search"] = "*", ["count"] = true, ["top"] = 0 }, ct);
            if (result["@odata.count"]?.GetValue<int>() == expected) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("The index document count has not converged. Check the index and rerun ingestion.");
    }

    public async Task VerifyIndexAsync(SourceManifest manifest, CancellationToken ct)
    {
        var index = await SendAsync(HttpMethod.Get, $"indexes/{settings.Search.IndexName}", null, ct);
        if (index["description"]?.GetValue<string>() != CorpusSignature(manifest))
            throw new InvalidOperationException("Embedding or corpus settings differ from this index. Use the matching configuration.");
    }

    public static JsonObject BuildQuery(Settings settings, string query, RetrievalMode mode, float[]? vector)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("Query must be 1–2000 characters.");
        var payload = new JsonObject
        {
            // Return the same chunk depth in all four modes, then collapse to source documents.
            ["top"] = settings.Retrieval.VectorCandidates,
            ["select"] = "id,sourceId,title,url,content",
            ["searchFields"] = "title,content"
        };
        if (mode != RetrievalMode.Vector) payload["search"] = query;
        if (mode != RetrievalMode.Keyword)
        {
            if (vector is null || vector.Length != settings.Foundry.Dimensions)
                throw new ArgumentException("A matching Foundry query embedding is required.");
            payload["vectorQueries"] = new JsonArray(new JsonObject
            {
                ["kind"] = "vector", ["vector"] = JsonSerializer.SerializeToNode(vector),
                ["fields"] = "contentVector", ["k"] = settings.Retrieval.VectorCandidates
            });
        }
        if (mode == RetrievalMode.Semantic)
        {
            payload["queryType"] = "semantic";
            payload["semanticConfiguration"] = settings.Search.SemanticConfiguration;
            payload["semanticErrorHandling"] = "fail";
            // Azure only permits semanticMaxWaitInMilliseconds with partial error handling.
            // Keep complete reranking required; HTTP request timeouts still apply.
        }
        return payload;
    }

    public async Task<SearchResult> SearchAsync(string query, RetrievalMode mode, float[]? vector, CancellationToken ct)
    {
        var payload = BuildQuery(settings, query, mode, vector);
        var watch = Stopwatch.StartNew();
        var response = await SendAsync(HttpMethod.Post, $"indexes/{settings.Search.IndexName}/docs/search", payload, ct);
        watch.Stop();
        var values = response["value"]?.AsArray() ?? throw new InvalidDataException("Search omitted results.");
        var hits = values.Select(x => new SearchHit(
            x!["id"]!.GetValue<string>(), x["sourceId"]!.GetValue<string>(), x["title"]!.GetValue<string>(),
            x["url"]!.GetValue<string>(), x["content"]!.GetValue<string>(), x["@search.score"]!.GetValue<double>(),
            x["@search.rerankerScore"]?.GetValue<double>())).ToArray();
        if (mode == RetrievalMode.Semantic &&
            (response["@search.semanticPartialResponseReason"] is not null || hits.Any(x => x.RerankerScore is null)))
            throw new InvalidDataException("Search did not complete semantic reranking; do not label these results as reranked.");
        return new SearchResult(mode, watch.Elapsed.TotalMilliseconds, hits);
    }
}
