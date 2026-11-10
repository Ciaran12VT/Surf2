using System.Text.Json.Serialization;
using Surf2.Models;

namespace Surf2.Storage.Relational;

public enum RelationalConnectionActivationMode
{
    SaveBootstrap,
    SaveBootstrapForRestart,
    CurrentProcessOnly
}

public sealed record RelationalConnectionActivationOptions
{
    public RelationalConnectionActivationMode Mode { get; init; } = RelationalConnectionActivationMode.SaveBootstrap;
    public Guid? ExpectedDatabaseIdentity { get; init; }
}

public enum RelationalConnectionActivationStatus
{
    NotReady,
    DestinationChanged,
    EnvironmentOverrideRequiresChoice,
    BootstrapSaved,
    CurrentProcessSelected
}

public sealed class RelationalConnectionActivationResult
{
    internal RelationalConnectionActivationResult(RelationalConnectionActivationStatus status,
        PersistenceFormatResult format, bool environmentOverrideActive, SqlServerConnectionOptions? processOptions = null)
    {
        Status = status;
        Format = format;
        EnvironmentOverrideActive = environmentOverrideActive;
        ProcessOptions = processOptions;
    }

    public RelationalConnectionActivationStatus Status { get; }
    public PersistenceFormatResult Format { get; }
    public bool EnvironmentOverrideActive { get; }
    public bool SettingsSaved => Status == RelationalConnectionActivationStatus.BootstrapSaved;
    public bool RequiresRestart => Status is RelationalConnectionActivationStatus.BootstrapSaved
        or RelationalConnectionActivationStatus.EnvironmentOverrideRequiresChoice;
    public bool RequiresEnvironmentChangeBeforeRestart => EnvironmentOverrideActive && RequiresRestart;

    /// <summary>Only CurrentProcessOnly supplies options. The caller must explicitly use them instead of CreateDefault.</summary>
    [JsonIgnore]
    public SqlServerConnectionOptions? ProcessOptions { get; }

    public override string ToString() => $"{nameof(RelationalConnectionActivationResult)}: {Status}";
}

/// <summary>Explicit Ready connection selection; never initializes a database, changes the environment, or hot-swaps a session.</summary>
public sealed class RelationalApplicationConnection
{
    private readonly LocalConnectionSettingsStore _settings;
    private readonly Func<string, CancellationToken, Task<PersistenceFormatResult>> _probe;
    private readonly Func<string?> _environmentOverride;

    public RelationalApplicationConnection(LocalConnectionSettingsStore? settings = null) : this(
        settings ?? new LocalConnectionSettingsStore(), new PersistenceFormatProbe().ProbeAsync,
        () => Environment.GetEnvironmentVariable(SqlServerConnectionOptions.EnvironmentVariableName))
    {
    }

    internal RelationalApplicationConnection(LocalConnectionSettingsStore settings,
        Func<string, CancellationToken, Task<PersistenceFormatResult>> probe, Func<string?> environmentOverride)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _environmentOverride = environmentOverride ?? throw new ArgumentNullException(nameof(environmentOverride));
    }

    /// <summary>
    /// Default SaveBootstrap refuses an active environment override without modifying settings.
    /// SaveBootstrapForRestart explicitly saves the selected target but requires the overriding environment to change before restart.
    /// CurrentProcessOnly returns validated options without saving settings or modifying the environment; existing sessions remain unchanged.
    /// </summary>
    public async Task<RelationalConnectionActivationResult> ActivateAsync(string connectionString,
        RelationalConnectionActivationOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new RelationalConnectionActivationOptions();
        if (!Enum.IsDefined(options.Mode) || options.ExpectedDatabaseIdentity == Guid.Empty)
            throw new ArgumentException("Invalid relational activation options.", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        SqlServerConnectionOptions selected = Normalize(connectionString);
        PersistenceFormatResult format = await _probe(selected.ConnectionString, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        bool overridden = !string.IsNullOrWhiteSpace(_environmentOverride());
        if (format.Format != PersistenceFormat.Relational || format.SchemaVersion != PersistenceFormatProbe.SupportedSchemaVersion ||
            !format.DatabaseIdentity.HasValue || format.DatabaseIdentity.Value == Guid.Empty)
            return new(RelationalConnectionActivationStatus.NotReady, format, overridden);
        if (options.ExpectedDatabaseIdentity.HasValue && options.ExpectedDatabaseIdentity != format.DatabaseIdentity)
            return new(RelationalConnectionActivationStatus.DestinationChanged, format, overridden);
        if (options.Mode == RelationalConnectionActivationMode.CurrentProcessOnly)
            return new(RelationalConnectionActivationStatus.CurrentProcessSelected, format, overridden, selected);
        if (overridden && options.Mode == RelationalConnectionActivationMode.SaveBootstrap)
            return new(RelationalConnectionActivationStatus.EnvironmentOverrideRequiresChoice, format, true);

        cancellationToken.ThrowIfCancellationRequested();
        _settings.Save(new PersistenceConnectionSettings { ConnectionString = selected.ConnectionString });
        // Do not report an effective switch if another thread introduced an override while the file was being published.
        return new(RelationalConnectionActivationStatus.BootstrapSaved, format, !string.IsNullOrWhiteSpace(_environmentOverride()));
    }

    private static SqlServerConnectionOptions Normalize(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A connection string is required.", nameof(connectionString));
        try { return SqlServerConnectionOptions.FromConnectionString(connectionString.Trim()); }
        catch (ArgumentException)
        {
            // Parser diagnostics can include a source token. Never expose credentials from that token or retain it as an inner exception.
            throw new ArgumentException("The connection string is invalid.", nameof(connectionString));
        }
    }
}
