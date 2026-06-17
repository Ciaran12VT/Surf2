using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Surf2.Storage;

namespace Surf2.Services;

public sealed class ExternalOpenPipeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly Func<ExternalOpenRequest, Task<ExternalOpenResponse>> _handler;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public ExternalOpenPipeServer(
        string pipeName,
        Func<ExternalOpenRequest, Task<ExternalOpenResponse>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public void Start()
    {
        _serverTask ??= Task.Run(RunAsync);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_cancellation.Token);
                await HandleConnectionAsync(pipe, _cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                InternalLogService.Warning(
                    "External-open pipe server recovered after an IPC failure.",
                    ("PipeName", _pipeName),
                    ("Error", ex.Message));
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

        string? requestJson = await reader.ReadLineAsync(cancellationToken);
        try
        {
            ExternalOpenRequest? request = string.IsNullOrWhiteSpace(requestJson)
                ? null
                : JsonSerializer.Deserialize<ExternalOpenRequest>(requestJson, JsonOptions);
            if (request == null)
            {
                InternalLogService.Warning("External-open request was empty or invalid.");
                return;
            }

            ExternalOpenResponse response = await _handler(request);
            if (!response.Success && !response.WasCancelled)
            {
                InternalLogService.Warning(
                    "External-open request was rejected by the running Surf2 instance.",
                    ("Message", response.Message),
                    ("ScopeId", request.ScopeId),
                    ("ScopeName", request.ScopeName),
                    ("ResourcePath", request.ResourcePath),
                    ("ResourceKind", request.ResourceKind));
            }
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "External-open request failed.");
        }
    }
}

public static class ExternalOpenPipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Task<ExternalOpenResponse?> TrySendAsync(
        ExternalOpenRequest request,
        TimeSpan connectTimeout)
    {
        string connectionString = ResolveConnectionString(request.ConnectionString);
        foreach (string pipeName in ExternalOpenPipeNames.CreateOpenRequestCandidates(
            connectionString,
            request.ScopeId,
            request.ScopeName))
        {
            if (TrySendToPipe(request, pipeName, connectTimeout))
            {
                return Task.FromResult<ExternalOpenResponse?>(
                    ExternalOpenResponse.Ok("External-open request was forwarded to an existing Surf2 instance."));
            }
        }

        return Task.FromResult<ExternalOpenResponse?>(null);
    }

    private static bool TrySendToPipe(
        ExternalOpenRequest request,
        string pipeName,
        TimeSpan connectTimeout)
    {
        InternalLogService.Info(
            "Attempting to forward external-open request to running Surf2 instance.",
            ("PipeName", pipeName),
            ("ConnectTimeoutMs", connectTimeout.TotalMilliseconds),
            ("ScopeId", request.ScopeId),
            ("ScopeName", request.ScopeName),
            ("ResourcePath", request.ResourcePath),
            ("ResourceKind", request.ResourceKind));

        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        try
        {
            pipe.Connect(ToTimeoutMilliseconds(connectTimeout));

            using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };

            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            writer.WriteLine(requestJson);
            InternalLogService.Info(
                "External-open request was forwarded to a running Surf2 instance.",
                ("PipeName", pipeName));
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            InternalLogService.Warning(
                "Could not forward external-open request to running Surf2 instance.",
                ("PipeName", pipeName),
                ("Error", ex.Message));
            return false;
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        return timeout.TotalMilliseconds >= int.MaxValue
            ? int.MaxValue
            : Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static string ResolveConnectionString(string connectionString)
    {
        return string.IsNullOrWhiteSpace(connectionString)
            ? SqlServerConnectionOptions.CreateDefault().ConnectionString
            : SqlServerConnectionOptions.FromConnectionString(connectionString).ConnectionString;
    }
}

public static class ExternalOpenPipeNames
{
    public const string AnyInstance = "Surf2.ExternalOpen.Any";

    public static string Create(string connectionString)
    {
        return CreateDatabase(connectionString);
    }

    public static string CreateDatabase(string connectionString)
    {
        string normalizedConnectionString;
        try
        {
            normalizedConnectionString = SqlServerConnectionOptions
                .FromConnectionString(connectionString)
                .ConnectionString
                .ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            normalizedConnectionString = connectionString.Trim().ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedConnectionString));
        return $"Surf2.ExternalOpen.{Convert.ToHexString(hash)[..16]}";
    }

    public static string? CreateActiveScope(string connectionString, string scopeId, string scopeName)
    {
        string scopeKey = !string.IsNullOrWhiteSpace(scopeId)
            ? scopeId.Trim()
            : scopeName.Trim();
        if (string.IsNullOrWhiteSpace(scopeKey))
        {
            return null;
        }

        string databasePipeName = CreateDatabase(connectionString);
        string normalizedScopeKey = scopeKey.ToUpperInvariant();
        string key = $"{databasePipeName}|{normalizedScopeKey}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"Surf2.ExternalOpen.Scope.{Convert.ToHexString(hash)[..16]}";
    }

    public static IEnumerable<string> CreateOpenRequestCandidates(
        string connectionString,
        string scopeId,
        string scopeName)
    {
        string? activeScopePipeName = CreateActiveScope(connectionString, scopeId, scopeName);
        if (!string.IsNullOrWhiteSpace(activeScopePipeName))
        {
            yield return activeScopePipeName;
        }

        yield return CreateDatabase(connectionString);
        yield return AnyInstance;
    }
}
