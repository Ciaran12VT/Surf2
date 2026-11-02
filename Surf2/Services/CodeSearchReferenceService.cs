using System.Text.Json;
using Surf2.Models;

namespace Surf2.Services;

public static class CodeSearchReferenceService
{
    private const string SchemePrefix = "surf-search:";
    private const string VersionPrefix = SchemePrefix + "v1:";

    public static string Encode(CodeSearchReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!IsValid(reference))
        {
            throw new ArgumentException("A search reference requires a document path and a nonempty search string.", nameof(reference));
        }

        string payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(reference))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return VersionPrefix + payload;
    }

    public static bool TryDecode(string address, out CodeSearchReference reference)
    {
        reference = new CodeSearchReference(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string trimmedAddress = address.Trim();
        if (!trimmedAddress.StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string payload = trimmedAddress[VersionPrefix.Length..];
        if (payload.Length == 0 || payload.Length % 4 == 1 ||
            payload.Any(character => !IsBase64UrlCharacter(character)))
        {
            return false;
        }

        string base64 = payload.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        try
        {
            CodeSearchReference? decoded = JsonSerializer.Deserialize<CodeSearchReference>(Convert.FromBase64String(base64));
            if (decoded == null || !IsValid(decoded))
            {
                return false;
            }

            reference = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    public static bool IsSearchReference(string address)
    {
        return !string.IsNullOrWhiteSpace(address) &&
               address.Trim().StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValid(CodeSearchReference reference)
    {
        return !string.IsNullOrWhiteSpace(reference.DocumentPath) &&
               reference.DocumentPath.IndexOfAny(['\0', '\r', '\n']) < 0 &&
               !string.IsNullOrEmpty(reference.SearchText);
    }

    private static bool IsBase64UrlCharacter(char character)
    {
        return character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
    }
}
