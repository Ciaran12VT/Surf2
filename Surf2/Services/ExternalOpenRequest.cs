namespace Surf2.Services;

public sealed class ExternalOpenRequest
{
    public string ConnectionString { get; set; } = string.Empty;

    public string ScopeId { get; set; } = string.Empty;

    public string ScopeName { get; set; } = string.Empty;

    public string ResourcePath { get; set; } = string.Empty;

    public string ResourceName { get; set; } = string.Empty;

    public string ResourceKind { get; set; } = string.Empty;

    public int? LineNumber { get; set; }

    public int? ColumnNumber { get; set; }

    public bool SuppressSavePrompt { get; set; }

    public bool HasTarget =>
        !string.IsNullOrWhiteSpace(ResourcePath) &&
        (!string.IsNullOrWhiteSpace(ScopeId) || !string.IsNullOrWhiteSpace(ScopeName));

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(ResourceName)
            ? ResourceName.Trim()
            : !string.IsNullOrWhiteSpace(ResourcePath)
                ? ResourcePath.Trim()
                : "Surf resource";
}

public sealed class ExternalOpenResponse
{
    public bool Success { get; set; }

    public bool WasCancelled { get; set; }

    public string Message { get; set; } = string.Empty;

    public static ExternalOpenResponse Ok(string message)
    {
        return new ExternalOpenResponse
        {
            Success = true,
            Message = message
        };
    }

    public static ExternalOpenResponse Fail(string message)
    {
        return new ExternalOpenResponse
        {
            Success = false,
            Message = message
        };
    }

    public static ExternalOpenResponse Cancelled(string message)
    {
        return new ExternalOpenResponse
        {
            Success = false,
            WasCancelled = true,
            Message = message
        };
    }
}
