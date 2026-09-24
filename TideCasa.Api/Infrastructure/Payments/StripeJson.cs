using System.Text.Json;
using System.Text.RegularExpressions;

namespace TideCasa.Api.Infrastructure.Payments;

internal static class StripeJson
{
    internal static JsonElement P(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : default;
    internal static string? S(JsonElement root, string name) => P(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    internal static bool? B(JsonElement root, string name) => P(root, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    internal static long? N(JsonElement root, string name) => P(root, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;
    internal static string? ObjectId(JsonElement root, string name) => P(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : S(P(root, name), "id");
    internal static bool Empty(JsonElement root, string name) => P(root, name).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;
    internal static bool Id(string? value, string prefix) => value is { Length: <= 200 } && Regex.IsMatch(value, "^" + prefix + "_[A-Za-z0-9_]+$", RegexOptions.CultureInvariant);
    internal static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            { if (!names.Add(field.Name)) throw new JsonException(); RejectDuplicateProperties(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
