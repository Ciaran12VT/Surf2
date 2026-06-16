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
        string connectionString,
        Func<ExternalOpenRequest, Task<ExternalOpenResponse>> handler)
    {
        _pipeName = ExternalOpenPipeNames.Create(connectionString);
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
                    PipeDirection.InOut,
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
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };

        string? requestJson = await reader.ReadLineAsync(cancellationToken);
        ExternalOpenResponse response;
        try
        {
            ExternalOpenRequest? request = string.IsNullOrWhiteSpace(requestJson)
                ? null
                : JsonSerializer.Deserialize<ExternalOpenRequest>(requestJson, JsonOptions);
            response = request == null
                ? ExternalOpenResponse.Fail("External-open request was empty or invalid.")
                : await _handler(request);
        }
        catch (Exception ex)
        {
            InternalLogService.Error(ex, "External-open request failed.");
            response = ExternalOpenResponse.Fail(ex.Message);
        }

        string responseJson = JsonSerializer.Serialize(response, JsonOptions);
        await writer.WriteLineAsync(responseJson);
    }
}

public static class ExternalOpenPipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<ExternalOpenResponse?> TrySendAsync(
        ExternalOpenRequest request,
        TimeSpan connectTimeout)
    {
        string connectionString = ResolveConnectionString(request.ConnectionString);
        string pipeName = ExternalOpenPipeNames.Create(connectionString);

        using var cancellation = new CancellationTokenSource(connectTimeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(cancellation.Token);

            await using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            await writer.WriteLineAsync(requestJson);
            string? responseJson = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return ExternalOpenResponse.Fail("Surf2 did not return an external-open response.");
            }

            ExternalOpenResponse? response = JsonSerializer.Deserialize<ExternalOpenResponse>(responseJson, JsonOptions);
            return response == null
                ? ExternalOpenResponse.Fail("Surf2 did not return an external-open response.")
                : response;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
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
    public static string Create(string connectionString)
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
}
