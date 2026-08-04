using System.Text.Json;

namespace Harbora.Infrastructure.Ai;

/// <summary>
/// Reads the token counts a provider reported.
///
/// Its own class because two callers need it: the adapter for a whole response, and the streaming
/// path for each frame — providers put usage on one of the last frames rather than only after
/// [DONE], so every chunk is inspected.
///
/// Counts are never estimated locally. A number we invent is a bill we cannot defend when a customer
/// queries it, and every provider tokenises differently.
/// </summary>
public static class AiUsageParser
{
    /// <summary>Input, output and cached-input tokens. Zeroes when the body says nothing.</summary>
    public static (long Input, long Output, long Cached) Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (0, 0, 0);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("usage", out var usage)) return (0, 0, 0);
            if (usage.ValueKind != JsonValueKind.Object) return (0, 0, 0);

            var input = Number(usage, "prompt_tokens", "input_tokens");
            var output = Number(usage, "completion_tokens", "output_tokens");

            long cached = 0;
            if (usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                cached = Number(details, "cached_tokens");
            }

            return (input, output, cached);
        }
        catch (JsonException)
        {
            // A body we cannot parse is charged as zero rather than guessed at. Under-billing a
            // malformed response is recoverable; inventing a number is not.
            return (0, 0, 0);
        }
    }

    private static long Number(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var number))
            {
                return Math.Max(0, number);
            }
        }

        return 0;
    }
}
