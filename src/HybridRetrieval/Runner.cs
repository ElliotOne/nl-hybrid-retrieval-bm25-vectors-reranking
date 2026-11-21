using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HybridRetrieval;

public sealed class Runner(Settings settings, Corpus corpus, FoundryEmbeddings embeddings, AzureSearch search)
{
    private static readonly RetrievalMode[] Modes = Enum.GetValues<RetrievalMode>();
    private static readonly JsonSerializerOptions ReportJson = new(Corpus.Json)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SourcesAsync(CancellationToken ct)
    {
        var (manifest, docs) = await corpus.DownloadAsync(ct);
        var queries = await Corpus.ReadAsync<QueryManifest>(settings.QueriesPath, ct);
        Evaluation.Validate(queries, manifest);
        foreach (var doc in docs)
            Console.WriteLine($"{doc.Source.Id,-28} {doc.Text.Length,6} chars  {Corpus.Split(doc, settings.Ingestion.ChunkCharacters, settings.Ingestion.OverlapCharacters).Length,3} chunks");
        Console.WriteLine($"Verified {docs.Length} official documents at {manifest.Revision}; {queries.Queries.Length} authored evaluation queries.");
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var (manifest, docs) = await corpus.DownloadAsync(ct);
        var chunks = docs.SelectMany(doc => Corpus.Split(doc, settings.Ingestion.ChunkCharacters, settings.Ingestion.OverlapCharacters)).ToArray();
        await search.EnsureIndexAsync(manifest, ct);
        foreach (var batch in chunks.Chunk(settings.Ingestion.EmbeddingBatchSize))
        {
            var vectors = await embeddings.EmbedAsync(batch.Select(x => x.Title + "\n\n" + x.Content).ToArray(), ct);
            await search.UploadAsync(batch, vectors, ct);
            Console.WriteLine($"Indexed {batch.Length} chunks into {settings.Search.IndexName}.");
        }
        await search.WaitForCountAsync(chunks.Length, ct);
        var run = new
        {
            createdAtUtc = DateTimeOffset.UtcNow, settings.Search.IndexName, corpusSignature = search.CorpusSignature(manifest),
            manifest.Revision, model = settings.Foundry.EmbeddingDeployment, settings.Foundry.Dimensions,
            documents = docs.Length, chunks = chunks.Length
        };
        var path = await SaveAsync("ingestion", run, ct);
        Console.WriteLine($"Ready: {chunks.Length} chunks from {docs.Length} documents. Receipt: {path}");
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        var manifest = await Corpus.ReadAsync<SourceManifest>(settings.SourcesPath, ct);
        await search.VerifyIndexAsync(manifest, ct);
    }

    public async Task CompareAsync(string query, CancellationToken ct)
    {
        await VerifyAsync(ct);
        var watch = Stopwatch.StartNew();
        var vector = (await embeddings.EmbedAsync([query], ct))[0];
        watch.Stop();
        Console.WriteLine($"Foundry query embedding: {watch.Elapsed.TotalMilliseconds:F1} ms (shared by three vector modes).");
        var results = new List<SearchResult>();
        foreach (var mode in Modes)
        {
            var result = await search.SearchAsync(query, mode, vector, ct);
            results.Add(result);
            Console.WriteLine($"\n{mode}: search {result.ElapsedMs:F1} ms");
            var sources = Evaluation.Sources(result.Hits, settings.Retrieval.TopK);
            for (var i = 0; i < sources.Length; i++)
            {
                var hit = sources[i];
                Console.WriteLine($"{i + 1}. {hit.Title} [{hit.SourceId}] score={hit.Score:F5}" +
                    (hit.RerankerScore is null ? "" : $" reranker={hit.RerankerScore:F3}"));
                Console.WriteLine($"   {hit.Url}\n   {hit.Content[..Math.Min(220, hit.Content.Length)].Replace('\n', ' ')}");
            }
        }
        Console.WriteLine("Report: " + await SaveAsync("comparison", new
        {
            createdAtUtc = DateTimeOffset.UtcNow, query, settings.Search.IndexName,
            settings.Foundry.EmbeddingDeployment, settings.Foundry.Dimensions,
            settings.Retrieval, embeddingMs = watch.Elapsed.TotalMilliseconds, results
        }, ct));
    }

    public async Task EvaluateAsync(int repetitions, CancellationToken ct)
    {
        if (repetitions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repetitions), "Use 1–20 repetitions.");
        var sourceManifest = await Corpus.ReadAsync<SourceManifest>(settings.SourcesPath, ct);
        var queryManifest = await Corpus.ReadAsync<QueryManifest>(settings.QueriesPath, ct);
        Evaluation.Validate(queryManifest, sourceManifest);
        await search.VerifyIndexAsync(sourceManifest, ct);
        var measurements = new List<Measurement>();
        for (var repetition = 0; repetition < repetitions; repetition++)
        for (var q = 0; q < queryManifest.Queries.Length; q++)
        {
            var query = queryManifest.Queries[q];
            var watch = Stopwatch.StartNew();
            var vector = (await embeddings.EmbedAsync([query.Query], ct))[0];
            watch.Stop();
            // Rotate order so the same mode does not always run first. No caching or fabricated responses.
            for (var m = 0; m < Modes.Length; m++)
            {
                var mode = Modes[(m + q + repetition) % Modes.Length];
                var result = await search.SearchAsync(query.Query, mode, vector, ct);
                var quality = Evaluation.Score(result.Hits, query.Relevance, settings.Retrieval.TopK);
                measurements.Add(new Measurement(query.Id, query.Category, query.Query, repetition + 1,
                    mode, mode == RetrievalMode.Keyword ? 0 : watch.Elapsed.TotalMilliseconds, result, quality));
                Console.WriteLine($"{repetition + 1}/{repetitions} {query.Id} {mode,-8} recall={quality.RecallAtK:F3} nDCG={quality.NdcgAtK:F3} search={result.ElapsedMs:F1}ms");
            }
        }
        var summary = measurements.GroupBy(x => x.Mode).Select(group => new
        {
            mode = group.Key, requests = group.Count(),
            recallAtK = group.Average(x => x.Quality.RecallAtK), mrr = group.Average(x => x.Quality.ReciprocalRank),
            ndcgAtK = group.Average(x => x.Quality.NdcgAtK),
            searchP50Ms = Evaluation.Percentile(group.Select(x => x.Result.ElapsedMs), .5),
            searchP95Ms = Evaluation.Percentile(group.Select(x => x.Result.ElapsedMs), .95),
            estimatedStandaloneP50Ms = Evaluation.Percentile(group.Select(x => x.EmbeddingMs + x.Result.ElapsedMs), .5)
        }).OrderBy(x => x.mode).ToArray();
        var run = new
        {
            createdAtUtc = DateTimeOffset.UtcNow, settings.Search.IndexName, settings.Search.ApiVersion,
            corpusRevision = sourceManifest.Revision, queryManifest.Version, queryManifest.JudgmentNotes,
            sourcesSha256 = Corpus.Hash(await File.ReadAllBytesAsync(Settings.InputPath(settings.SourcesPath), ct)),
            queriesSha256 = Corpus.Hash(await File.ReadAllBytesAsync(Settings.InputPath(settings.QueriesPath), ct)),
            embeddingDeployment = settings.Foundry.EmbeddingDeployment, settings.Foundry.Dimensions,
            settings.Retrieval, settings.Ingestion, repetitions, summary, measurements
        };
        var path = await SaveAsync("evaluation", run, ct);
        var markdown = new StringBuilder("# Live retrieval comparison\n\n");
        markdown.AppendLine($"Index: `{settings.Search.IndexName}`. Source revision: `{sourceManifest.Revision}`. K={settings.Retrieval.TopK}; {repetitions} repetition(s).\n");
        markdown.AppendLine("Scores use unique source documents in returned order, with the highest-ranked chunk representing each source. Labels are authored seed judgments, not independent human ground truth.\n");
        markdown.AppendLine("| Mode | Recall@K | MRR | nDCG@K | Search p50 ms | Search p95 ms | Estimated standalone p50 ms |\n| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in summary)
            markdown.AppendLine(FormattableString.Invariant($"| {s.mode} | {s.recallAtK:F3} | {s.mrr:F3} | {s.ndcgAtK:F3} | {s.searchP50Ms:F1} | {s.searchP95Ms:F1} | {s.estimatedStandaloneP50Ms:F1} |"));
        markdown.AppendLine("\nSearch latency includes client networking, authentication and retries. Estimated standalone latency adds the shared query embedding observation to vector-based modes; it is not an independent end-to-end load test. A small run does not establish a production p95.\n");
        markdown.AppendLine("Compare by query category and inspect source passages before selecting a strategy. BM25, RRF and semantic scores have different meanings and scales. No release or policy decision is made by this report.");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".md"), markdown.ToString(), ct);
        Console.WriteLine($"Report: {path}\nSummary: {Path.ChangeExtension(path, ".md")}");
    }

    private async Task<string> SaveAsync<T>(string prefix, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(settings.ArtifactsDirectory);
        var path = Path.Combine(settings.ArtifactsDirectory, $"{prefix}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, ReportJson), ct);
        return path;
    }
}
