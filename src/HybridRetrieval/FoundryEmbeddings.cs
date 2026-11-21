using System.Text.Json.Nodes;

namespace HybridRetrieval;

public sealed class FoundryEmbeddings(Settings settings, CloudHttp cloud)
{
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        if (inputs.Count == 0) return [];
        var payload = new JsonObject
        {
            ["model"] = settings.Foundry.EmbeddingDeployment,
            ["input"] = new JsonArray(inputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["dimensions"] = settings.Foundry.Dimensions,
            ["encoding_format"] = "float"
        };
        var result = await cloud.SendAsync(HttpMethod.Post,
            new Uri(settings.Foundry.Endpoint.TrimEnd('/') + "/openai/v1/embeddings"), payload,
            settings.Foundry.ApiKeyEnvironmentVariable, "https://cognitiveservices.azure.com/.default", ct);
        return Parse(result, inputs.Count, settings.Foundry.Dimensions);
    }

    public static float[][] Parse(JsonObject response, int count, int dimensions)
    {
        var data = response["data"]?.AsArray() ?? throw new InvalidDataException("Foundry omitted embeddings.");
        if (data.Count != count) throw new InvalidDataException("Foundry returned the wrong embedding count.");
        var vectors = new float[count][];
        foreach (var item in data)
        {
            var index = item?["index"]?.GetValue<int>() ?? -1;
            if (index < 0 || index >= count || vectors[index] is not null)
                throw new InvalidDataException("Foundry returned duplicate or invalid embedding indices.");
            var vector = item?["embedding"]?.AsArray().Select(x => x!.GetValue<float>()).ToArray()
                ?? throw new InvalidDataException("Foundry omitted a vector.");
            if (vector.Length != dimensions || vector.Any(x => !float.IsFinite(x)) || vector.All(x => x == 0))
                throw new InvalidDataException("Foundry returned an invalid embedding dimension or value.");
            vectors[index] = vector;
        }
        return vectors;
    }
}
