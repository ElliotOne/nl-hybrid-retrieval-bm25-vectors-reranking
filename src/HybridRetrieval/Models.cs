namespace HybridRetrieval;

public enum RetrievalMode { Keyword, Vector, Hybrid, Semantic }
public sealed record SourceDefinition(string Id, string Title, string Path, string Url, string Sha256);
public sealed record SourceManifest(string Repository, string Revision, string License, SourceDefinition[] Sources);
public sealed record SourceDocument(SourceDefinition Source, string Text);
public sealed record Chunk(string Id, string SourceId, string Title, string Url, int Ordinal, string Content);
public sealed record SearchHit(string Id, string SourceId, string Title, string Url, string Content,
    double Score, double? RerankerScore);
public sealed record SearchResult(RetrievalMode Mode, double ElapsedMs, SearchHit[] Hits);
public sealed record QueryDefinition(string Id, string Category, string Query, Dictionary<string, int> Relevance);
public sealed record QueryManifest(string Version, string JudgmentNotes, QueryDefinition[] Queries);
public sealed record Quality(double RecallAtK, double ReciprocalRank, double NdcgAtK);
public sealed record Measurement(string QueryId, string Category, string Query, int Repetition,
    RetrievalMode Mode, double EmbeddingMs, SearchResult Result, Quality Quality);
