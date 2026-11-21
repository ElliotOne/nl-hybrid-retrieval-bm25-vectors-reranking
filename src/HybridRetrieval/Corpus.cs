using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HybridRetrieval;

public sealed class Corpus(HttpClient http, Settings settings)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(Settings.InputPath(path));
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct) ?? throw new InvalidDataException($"Empty file: {path}");
    }

    public async Task<(SourceManifest Manifest, SourceDocument[] Documents)> DownloadAsync(CancellationToken ct)
    {
        var manifest = await ReadAsync<SourceManifest>(settings.SourcesPath, ct);
        if (manifest.Repository != "dotnet/docs" || !Regex.IsMatch(manifest.Revision, "^[a-f0-9]{40}$") ||
            manifest.Sources.Length == 0 || manifest.Sources.Select(x => x.Id).Distinct().Count() != manifest.Sources.Length)
            throw new InvalidDataException("Use a pinned dotnet/docs revision and unique source IDs.");
        var docs = new List<SourceDocument>();
        foreach (var source in manifest.Sources)
        {
            if (!Regex.IsMatch(source.Id, "^[a-z0-9-]+$") || !source.Path.StartsWith("docs/", StringComparison.Ordinal) ||
                source.Path.Contains("..") || source.Path.Contains('?') || source.Path.Contains('#'))
                throw new InvalidDataException("Invalid source ID or path.");
            var url = $"https://raw.githubusercontent.com/{manifest.Repository}/{manifest.Revision}/{source.Path}";
            var bytes = await http.GetByteArrayAsync(url, ct);
            if (bytes.Length is < 100 or > 2_000_000 || Hash(bytes) != source.Sha256)
                throw new InvalidDataException($"Source content changed or is invalid: {source.Id}.");
            docs.Add(new SourceDocument(source, Clean(Encoding.UTF8.GetString(bytes))));
        }
        return (manifest, docs.ToArray());
    }

    public static string Clean(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n");
        text = Regex.Replace(text, @"\A---\n[\s\S]*?\n---\n", "");
        text = Regex.Replace(text, @"<!--([\s\S]*?)-->", "");
        // Source includes are not expanded; do not present their paths as retrieved prose.
        text = Regex.Replace(text, @"(?im)^.*\[!(?:include|code)[^\n]*$", "");
        text = Regex.Replace(text, @"\[([^\]\n]+)\]\([^\)\n]+\)", "$1");
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    public static Chunk[] Split(SourceDocument doc, int size, int overlap)
    {
        if (size <= 0 || overlap < 0 || overlap >= size) throw new ArgumentOutOfRangeException(nameof(size));
        var chunks = new List<Chunk>();
        var start = 0;
        while (start < doc.Text.Length)
        {
            var end = Math.Min(start + size, doc.Text.Length);
            if (end < doc.Text.Length)
            {
                var paragraph = doc.Text.LastIndexOf("\n\n", end - 1, end - start, StringComparison.Ordinal);
                if (paragraph > start + size / 2) end = paragraph;
            }
            var content = doc.Text[start..end].Trim();
            if (content.Length > 0)
            {
                var id = doc.Source.Id + "-" + Hash(Encoding.UTF8.GetBytes($"{chunks.Count}:{content}"))[..16];
                chunks.Add(new Chunk(id, doc.Source.Id, doc.Source.Title, doc.Source.Url, chunks.Count, content));
            }
            if (end == doc.Text.Length) break;
            start = Math.Max(start + 1, end - overlap);
        }
        return chunks.ToArray();
    }

    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
