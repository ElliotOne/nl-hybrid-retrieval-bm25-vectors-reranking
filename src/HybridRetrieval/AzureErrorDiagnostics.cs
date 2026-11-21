using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HybridRetrieval;

public static class AzureErrorDiagnostics
{
    private const int MaxBodyBytes = 16_384;

    public static async Task<string> ReadAsync(HttpResponseMessage response, string?[] secrets, CancellationToken ct)
    {
        var lines = new List<string>();
        foreach (var header in new[] { "request-id", "x-ms-request-id", "apim-request-id" })
            if (response.Headers.TryGetValues(header, out var values))
                lines.Add($"Azure {header}: {Safe(string.Join(", ", values), secrets, 200)}");
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[MaxBodyBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxBodyBytes)
                lines.Add("Azure error body exceeded the diagnostic size limit; body omitted.");
            else
            {
                var root = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 0, count));
                var error = root?["error"] as JsonObject;
                if (error is null) lines.Add("Azure returned no structured error; body omitted.");
                else
                {
                    AddError(lines, error, "Azure", secrets);
                    if (error["details"] is JsonArray details)
                        foreach (var detail in details.Take(5).OfType<JsonObject>())
                            AddError(lines, detail, "Azure detail", secrets);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or HttpRequestException)
        {
            lines.Add("Azure error details could not be read as structured JSON; body omitted.");
        }
        return string.Join('\n', lines);
    }

    private static void AddError(List<string> lines, JsonObject error, string label, string?[] secrets)
    {
        if (error["code"] is JsonValue code && code.TryGetValue<string>(out var codeText))
            lines.Add($"{label} error code: {Safe(codeText, secrets, 200)}");
        if (error["message"] is JsonValue message && message.TryGetValue<string>(out var messageText))
            lines.Add($"{label} error message: {Safe(messageText, secrets, 2000)}");
    }

    private static string Safe(string text, string?[] secrets, int maxLength)
    {
        foreach (var secret in secrets.Where(x => !string.IsNullOrEmpty(x)).OrderByDescending(x => x!.Length))
            text = text.Replace(secret!, "[REDACTED]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"[\p{Cc}\p{Cf}]+", " ");
        return text.Length <= maxLength ? text : text[..maxLength] + " [truncated]";
    }
}
