using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Surf2.Services;

internal static class InternalLogService
{
    private const int RetainedLogFiles = 25;
    private static readonly object SyncRoot = new();
    private static bool _isEnabled;
    private static bool _initialized;
    private static string? _logDirectory;
    private static string? _logFilePath;

    public static bool IsEnabled => _isEnabled;

    public static string LogDirectory
    {
        get
        {
            EnsureInitialized();
            return _logDirectory ?? string.Empty;
        }
    }

    public static string LogFilePath
    {
        get
        {
            EnsureInitialized();
            return _logFilePath ?? string.Empty;
        }
    }

    public static void Initialize()
    {
        Configure(enabled: true);
    }

    public static void Configure(bool enabled)
    {
        if (enabled)
        {
            _isEnabled = true;
            EnsureInitialized();
            Info(
                "Internal logging enabled.",
                ("LogFile", _logFilePath),
                ("LogDirectory", _logDirectory));
            return;
        }

        if (_isEnabled)
        {
            Write(
                "INFO",
                "Internal logging disabled.",
                null,
                force: true,
                ("LogFile", _logFilePath),
                ("LogDirectory", _logDirectory));
        }

        _isEnabled = false;
    }

    public static void Info(string message, params (string Key, object? Value)[] details)
    {
        Write("INFO", message, null, details);
    }

    public static void Warning(string message, params (string Key, object? Value)[] details)
    {
        Write("WARN", message, null, details);
    }

    public static void Error(Exception exception, string message, params (string Key, object? Value)[] details)
    {
        Write("ERROR", message, exception, details);
    }

    public static void Fatal(Exception exception, string message, params (string Key, object? Value)[] details)
    {
        Write("FATAL", message, exception, details);
    }

    public static void Fatal(string message, params (string Key, object? Value)[] details)
    {
        Write("FATAL", message, null, details);
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            string logDirectory = ResolveWritableLogDirectory();
            Directory.CreateDirectory(logDirectory);

            _logDirectory = logDirectory;
            _logFilePath = Path.Combine(
                logDirectory,
                $"surf2-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            _initialized = true;

            PruneOldLogs(logDirectory);
            Write(
                "INFO",
                "Internal logging initialized.",
                null,
                force: true,
                ("LogFile", _logFilePath),
                ("BaseDirectory", AppContext.BaseDirectory),
                ("CurrentDirectory", Environment.CurrentDirectory),
                ("ProcessPath", Environment.ProcessPath),
                ("ProcessId", Environment.ProcessId),
                ("Framework", Environment.Version));
        }
    }

    private static string ResolveWritableLogDirectory()
    {
        foreach (string candidate in GetCandidateLogDirectories())
        {
            try
            {
                Directory.CreateDirectory(candidate);
                string probePath = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probePath, "ok", Encoding.UTF8);
                File.Delete(probePath);
                return candidate;
            }
            catch
            {
                // Try the next candidate. Logging should never block app startup.
            }
        }

        return Path.GetTempPath();
    }

    private static IEnumerable<string> GetCandidateLogDirectories()
    {
        string? solutionRoot = FindSolutionRoot();
        if (!string.IsNullOrWhiteSpace(solutionRoot))
        {
            yield return Path.Combine(solutionRoot, "internal-logs");
        }

        yield return Path.Combine(AppContext.BaseDirectory, "internal-logs");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Surf2", "internal-logs");
        }
    }

    private static string? FindSolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? projectRoot = null;

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Surf2.sln")))
            {
                return directory.FullName;
            }

            if (projectRoot == null && File.Exists(Path.Combine(directory.FullName, "Surf2.csproj")))
            {
                projectRoot = directory.FullName;
            }

            directory = directory.Parent;
        }

        return projectRoot;
    }

    private static void PruneOldLogs(string logDirectory)
    {
        try
        {
            DirectoryInfo directory = new(logDirectory);
            FileInfo[] oldLogs = directory
                .EnumerateFiles("surf2-*.log")
                .OrderByDescending(file => file.CreationTimeUtc)
                .Skip(RetainedLogFiles)
                .ToArray();

            foreach (FileInfo oldLog in oldLogs)
            {
                oldLog.Delete();
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void Write(string level, string message, Exception? exception, params (string Key, object? Value)[] details)
    {
        Write(level, message, exception, force: false, details);
    }

    private static void Write(
        string level,
        string message,
        Exception? exception,
        bool force = false,
        params (string Key, object? Value)[] details)
    {
        if (!force && !_isEnabled)
        {
            return;
        }

        try
        {
            EnsureInitialized();

            string? path = _logFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string entry = FormatEntry(level, message, exception, details);
            lock (SyncRoot)
            {
                File.AppendAllText(path, entry, Encoding.UTF8);
            }

            Debug.WriteLine(entry);
        }
        catch
        {
            // Logging must never be the reason the app fails.
        }
    }

    private static string FormatEntry(
        string level,
        string message,
        Exception? exception,
        params (string Key, object? Value)[] details)
    {
        StringBuilder builder = new();
        builder.AppendLine("--------------------------------------------------------------------------------");
        builder.Append(DateTimeOffset.Now.ToString("O"));
        builder.Append(" [");
        builder.Append(level);
        builder.Append("] ");
        builder.Append(message);
        builder.Append(" (pid ");
        builder.Append(Environment.ProcessId);
        builder.Append(", thread ");
        builder.Append(Environment.CurrentManagedThreadId);
        builder.AppendLine(")");

        foreach ((string key, object? value) in details)
        {
            builder.Append("  ");
            builder.Append(key);
            builder.Append(": ");
            builder.AppendLine(value?.ToString() ?? "<null>");
        }

        if (exception != null)
        {
            builder.AppendLine("  Exception:");
            builder.AppendLine(exception.ToString());
        }

        return builder.ToString();
    }
}
