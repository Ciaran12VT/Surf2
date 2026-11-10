using System.Text.Json;

namespace Surf2.Storage.Relational.Capture;

internal static class CaptureRowFidelity
{
    internal static bool Matches(JsonElement expected, JsonElement stored)
    {
        if (expected.ValueKind == JsonValueKind.Undefined || expected.ValueKind != stored.ValueKind) return false;
        if (expected.ValueKind != JsonValueKind.Object)
            return string.Equals(expected.GetRawText(), stored.GetRawText(), StringComparison.Ordinal);

        // Only the containing row's whitespace/name-escape spelling may change
        // during reconstruction. Property names/order and every value token
        // (including structured exception tokens) must match exactly.
        var sourceProperties = expected.EnumerateObject();
        var storedProperties = stored.EnumerateObject();
        while (sourceProperties.MoveNext())
        {
            if (!storedProperties.MoveNext()) return false;
            var source = sourceProperties.Current;
            var actual = storedProperties.Current;
            if (!string.Equals(source.Name, actual.Name, StringComparison.Ordinal) ||
                source.Value.ValueKind != actual.Value.ValueKind ||
                !string.Equals(source.Value.GetRawText(), actual.Value.GetRawText(), StringComparison.Ordinal))
                return false;
        }
        return !storedProperties.MoveNext();
    }
}
