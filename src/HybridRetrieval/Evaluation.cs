namespace HybridRetrieval;

public static class Evaluation
{
    public static SearchHit[] Sources(IEnumerable<SearchHit> hits, int k) => hits
        .DistinctBy(x => x.SourceId, StringComparer.Ordinal).Take(k).ToArray();

    public static Quality Score(IEnumerable<SearchHit> hits, IReadOnlyDictionary<string, int> relevance, int k)
    {
        if (k < 1 || relevance.Count == 0 || relevance.Values.Any(x => x is < 1 or > 3))
            throw new ArgumentException("Use a positive K and at least one source judgment graded 1–3.");
        var ranked = Sources(hits, k);
        var grades = ranked.Select(x => relevance.GetValueOrDefault(x.SourceId, 0)).ToArray();
        var recall = grades.Count(x => x > 0) / (double)relevance.Count;
        var first = Array.FindIndex(grades, x => x > 0);
        var dcg = grades.Select((grade, i) => (Math.Pow(2, grade) - 1) / Math.Log2(i + 2)).Sum();
        var ideal = relevance.Values.OrderDescending().Take(k)
            .Select((grade, i) => (Math.Pow(2, grade) - 1) / Math.Log2(i + 2)).Sum();
        return new Quality(recall, first < 0 ? 0 : 1.0 / (first + 1), dcg / ideal);
    }

    public static double Percentile(IEnumerable<double> observations, double percentile)
    {
        var values = observations.Order().ToArray();
        if (values.Length == 0 || percentile is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(percentile));
        return values[Math.Max(0, (int)Math.Ceiling(percentile * values.Length) - 1)];
    }

    public static void Validate(QueryManifest queries, SourceManifest sources)
    {
        var ids = sources.Sources.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        if (queries.Queries.Length == 0 || queries.Queries.Select(x => x.Id).Distinct().Count() != queries.Queries.Length)
            throw new InvalidDataException("Evaluation queries need unique IDs.");
        foreach (var query in queries.Queries)
            if (string.IsNullOrWhiteSpace(query.Query) || query.Relevance.Count == 0 ||
                query.Relevance.Any(x => !ids.Contains(x.Key) || x.Value is < 1 or > 3))
                throw new InvalidDataException($"Invalid source judgments for {query.Id}.");
    }
}
