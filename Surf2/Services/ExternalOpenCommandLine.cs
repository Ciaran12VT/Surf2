using System.Text;
using System.Text.Json;

namespace Surf2.Services;

public static class ExternalOpenCommandLine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(IReadOnlyList<string> args, out ExternalOpenRequest request)
    {
        request = new ExternalOpenRequest();
        if (args.Count == 0)
        {
            return false;
        }

        bool sawOpenMarker = false;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.IsNullOrWhiteSpace(arg))
            {
                continue;
            }

            if (IsOpenMarker(arg))
            {
                sawOpenMarker = true;
                continue;
            }

            if (TryReadJsonRequest(args, ref index, arg, out ExternalOpenRequest? jsonRequest) &&
                jsonRequest != null)
            {
                request = jsonRequest;
                return request.HasTarget;
            }

            if (!TrySplitOption(args, ref index, arg, out string name, out string value))
            {
                continue;
            }

            ApplyOption(request, name, value);
        }

        return request.HasTarget || (sawOpenMarker && !string.IsNullOrWhiteSpace(request.ResourcePath));
    }

    private static bool IsOpenMarker(string arg)
    {
        return string.Equals(arg, "--surf2-open", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arg, "--open-surf-resource", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(arg, "--external-open", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadJsonRequest(
        IReadOnlyList<string> args,
        ref int index,
        string arg,
        out ExternalOpenRequest? request)
    {
        request = null;
        if (!TrySplitOption(args, ref index, arg, out string name, out string value) ||
            !string.Equals(name, "surf2-open-json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string json = TryDecodeBase64(value, out string decoded)
            ? decoded
            : value;
        request = JsonSerializer.Deserialize<ExternalOpenRequest>(json, JsonOptions);
        return request != null;
    }

    private static bool TrySplitOption(
        IReadOnlyList<string> args,
        ref int index,
        string arg,
        out string name,
        out string value)
    {
        name = string.Empty;
        value = string.Empty;

        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string option = arg[2..];
        int separatorIndex = option.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            name = option[..separatorIndex];
            value = option[(separatorIndex + 1)..];
            return true;
        }

        name = option;
        if (index + 1 >= args.Count)
        {
            return true;
        }

        value = args[++index];
        return true;
    }

    private static void ApplyOption(ExternalOpenRequest request, string name, string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "connection-string":
            case "surf2-connection-string":
                request.ConnectionString = value;
                break;

            case "scope-id":
            case "surf-scope-id":
            case "scope":
                request.ScopeId = value;
                break;

            case "scope-name":
                request.ScopeName = value;
                break;

            case "resource-path":
            case "path":
                request.ResourcePath = value;
                break;

            case "resource-name":
            case "name":
                request.ResourceName = value;
                break;

            case "resource-kind":
            case "kind":
                request.ResourceKind = value;
                break;

            case "line":
            case "line-number":
                request.LineNumber = TryParsePositiveInt(value);
                break;

            case "column":
            case "column-number":
                request.ColumnNumber = TryParsePositiveInt(value);
                break;
        }
    }

    private static int? TryParsePositiveInt(string value)
    {
        return int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            decoded = Encoding.UTF8.GetString(bytes);
            return decoded.TrimStart().StartsWith("{", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
