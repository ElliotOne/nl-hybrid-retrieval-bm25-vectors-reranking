using System.Text.RegularExpressions;

namespace HybridRetrieval;

public sealed class Settings
{
    public SearchSettings Search { get; set; } = new();
    public FoundrySettings Foundry { get; set; } = new();
    public RetrievalSettings Retrieval { get; set; } = new();
    public IngestionSettings Ingestion { get; set; } = new();
    public string SourcesPath { get; set; } = "data/sources.json";
    public string QueriesPath { get; set; } = "data/queries.json";
    public string ArtifactsDirectory { get; set; } = "artifacts";

    public void Validate(bool cloud)
    {
        if (Ingestion.ChunkCharacters is < 500 or > 6000 || Ingestion.OverlapCharacters < 0 ||
            Ingestion.OverlapCharacters >= Ingestion.ChunkCharacters / 2 || Ingestion.EmbeddingBatchSize is < 1 or > 16)
            throw new ArgumentException("Use chunks of 500–6000 characters, overlap below half a chunk, and batches of 1–16.");
        if (Retrieval.TopK is < 1 or > 20 || Retrieval.VectorCandidates is < 50 or > 1000)
            throw new ArgumentException("TopK must be 1–20; VectorCandidates must be 50–1000 for semantic ranking.");
        if (Foundry.Dimensions is < 1 or > 3072 || string.IsNullOrWhiteSpace(Foundry.EmbeddingDeployment))
            throw new ArgumentException("Set an embedding deployment and its dimension count.");
        if (!Regex.IsMatch(Search.IndexName, "^[a-z0-9][a-z0-9-]{1,126}[a-z0-9]$") || Search.IndexName.Contains("--"))
            throw new ArgumentException("Use a valid Azure Search index name (3–128 lowercase letters, numbers, hyphens).");
        if (cloud)
        {
            CloudEndpoint(Search.Endpoint, ".search.windows.net");
            CloudEndpoint(Foundry.Endpoint, ".openai.azure.com", ".services.ai.azure.com", ".cognitiveservices.azure.com");
        }
    }

    private static void CloudEndpoint(string value, params string[] suffixes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !suffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Configure the HTTPS Azure resource root endpoint, without /openai/v1 or a project path.");
    }

    public static string InputPath(string path) => Path.IsPathRooted(path) || File.Exists(path)
        ? path : Path.Combine(AppContext.BaseDirectory, path);
}

public sealed class SearchSettings
{
    public string Endpoint { get; set; } = "";
    public string IndexName { get; set; } = "";
    public string ApiVersion { get; set; } = "2025-09-01";
    public string ApiKeyEnvironmentVariable { get; set; } = "HYBRID_SEARCH_API_KEY";
    public string SemanticConfiguration { get; set; } = "docs-semantic";
}
public sealed class FoundrySettings
{
    public string Endpoint { get; set; } = "";
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 1536;
    public string ApiKeyEnvironmentVariable { get; set; } = "HYBRID_FOUNDRY_API_KEY";
}
public sealed class RetrievalSettings
{
    public int TopK { get; set; } = 5;
    public int VectorCandidates { get; set; } = 50;
}
public sealed class IngestionSettings
{
    public int ChunkCharacters { get; set; } = 2200;
    public int OverlapCharacters { get; set; } = 250;
    public int EmbeddingBatchSize { get; set; } = 8;
}
