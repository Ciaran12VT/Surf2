using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Migration;

public sealed record RelationalMigrationRequest(string SourceConnectionString, string DestinationConnectionString,
    string LocalAppDataDirectory, string StagingRoot, bool CreateDestination = false, string? ResumeStageDirectory = null,
    bool UseLocalSource = false, string? SourcePackagePath = null);
public sealed record RelationalMigrationResult(Guid MigrationIdentity, string DatabaseName, long CapturedRows,
    string StageDirectory, bool AlreadyCompleted = false);

public sealed class RelationalMigrationException(string stageDirectory, Exception cause) : InvalidOperationException(
    "Conversion was not activated. The original database is unchanged. Recovery staging: " + stageDirectory, cause)
{
    public string StageDirectory { get; } = stageDirectory;
}

/// <summary>Explicit side-by-side conversion; never changes connection settings or the original database.</summary>
public sealed class RelationalMigrator
{
    public async Task<RelationalMigrationResult> ConvertAsync(RelationalMigrationRequest request,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool packageSource = request.SourcePackagePath != null;
        if (packageSource && request.UseLocalSource)
            throw new ArgumentException("Select one legacy source: a package, local files, or SQL.", nameof(request));
        string expectedOrigin = packageSource ? "Package" : request.UseLocalSource ? "Local" : "Sql";
        await using var stage = request.ResumeStageDirectory == null
            ? packageSource
                ? await LegacySourceStage.CapturePackageAsync(request.SourcePackagePath!, request.StagingRoot, cancellationToken)
                : request.UseLocalSource
                ? await LegacySourceStage.CaptureLocalAsync(request.LocalAppDataDirectory, request.StagingRoot, cancellationToken)
                : await LegacySourceStage.CaptureAsync(request.SourceConnectionString, request.LocalAppDataDirectory,
                    request.StagingRoot, progress, cancellationToken)
            : await LegacySourceStage.ReopenAsync(request.ResumeStageDirectory, cancellationToken);
        stage.RetainForRecovery = true;
        try
        {
            if (stage.SourceOrigin != expectedOrigin)
                throw new InvalidDataException("The recovery staging belongs to a different source origin.");
            if (packageSource && !string.Equals(Path.GetFullPath(request.SourcePackagePath!), stage.PackagePath,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recovery staging belongs to a different source package.");
            string sourceImageDirectory = stage.SourceImageDirectory
                ?? throw new InvalidDataException("The recovery manifest does not identify the original image directory. Recapture the legacy source before conversion.");
            string? sourceConnection = expectedOrigin == "Sql" ? request.SourceConnectionString : null;
            var inventory = new List<LegacyInventory>();
            foreach (string key in LegacySourceStage.Documents.Keys)
            {
                progress?.Report("Checking source fields: " + key);
                await using var cursor = stage.OpenDocument(key);
                inventory.Add(await new LegacySchemaInspector().InspectAsync(key, cursor, cancellationToken));
            }
            progress?.Report("Freezing referenced pasted images before destination writes");
            await stage.FreezeReferencedPastedImagesAsync(cancellationToken);
            await stage.WithVerifiedSourceAsync(sourceConnection, () => Task.CompletedTask, cancellationToken);
            long rows = inventory.Sum(item => item.DataRows);
            var installer = new RelationalSchemaInstaller();
            if (request.CreateDestination)
            {
                if (request.ResumeStageDirectory != null) throw new InvalidOperationException("Resume requires the existing destination, not database creation.");
                progress?.Report("Creating the separately selected destination");
                if (sourceConnection == null)
                    await installer.CreateEmptyDatabaseAsync(request.DestinationConnectionString, cancellationToken);
                else
                    await installer.CreateEmptyDestinationAsync(request.SourceConnectionString, request.DestinationConnectionString, cancellationToken);
            }
            var probe = new PersistenceFormatProbe();
            var source = sourceConnection == null ? null : await probe.ProbeAsync(sourceConnection, cancellationToken);
            var destination = await probe.ProbeAsync(request.DestinationConnectionString, cancellationToken);
            if (source != null && PersistenceFormatProbe.SameDatabase(source, destination)) throw new InvalidOperationException("The destination is the source database.");
            if (source != null && source.Format != PersistenceFormat.Legacy) throw new InvalidOperationException("The source is no longer a supported legacy database.");
            var session = new RelationalSession(request.DestinationConnectionString);
            await using var destinationLease = await MigrationTargetLease.AcquireAsync(session, cancellationToken);
            if (destination.Format == PersistenceFormat.Empty)
            {
                progress?.Report("Installing relational tables");
                if (packageSource)
                    await installer.InitializePackageDestinationAsync(request.DestinationConnectionString,
                        stage.MigrationIdentity, stage.Fingerprint, cancellationToken);
                else if (request.UseLocalSource)
                    await installer.InitializeLocalDestinationAsync(request.DestinationConnectionString,
                        stage.MigrationIdentity, stage.Fingerprint, cancellationToken);
                else
                    await installer.InitializeDestinationAsync(request.SourceConnectionString, request.DestinationConnectionString,
                        stage.MigrationIdentity, stage.Fingerprint, cancellationToken);
            }
            else if (destination.Format is PersistenceFormat.Incomplete or PersistenceFormat.Relational)
            {
                bool complete = await VerifyRecoveryAsync(session, stage, cancellationToken);
                if (complete) return new(stage.MigrationIdentity, destination.DatabaseName, rows, stage.DirectoryPath, AlreadyCompleted: true);
                if (request.ResumeStageDirectory == null) throw new InvalidOperationException("An incomplete destination must be resumed with its matching staging manifest.");
            }
            else throw new InvalidOperationException("The destination is not a verified empty database or a compatible recovery destination: " + destination.Reason);

            var journal = new MigrationJournal(session, stage);
            await journal.RecordProvenanceAsync(inventory, cancellationToken);
            progress?.Report("Converting captured databases and reverse history");
            await new LegacySnapshotImporter(session, journal, stage).ImportAsync(cancellationToken);
            progress?.Report("Converting scopes, diagrams, images, workbenches and preferences");
            var stateImporter = new LegacyStateImporter(session, journal, stage,
                sourceImageDirectory);
            await stateImporter.ImportAsync(cancellationToken);
            await ResolveSavedDocumentBindingsAsync(session, stage, cancellationToken);
            progress?.Report("Validating preservation and publishing the destination");
            var validationSession = RelationalSession.ForMigrationValidation(request.DestinationConnectionString,
                stage.MigrationIdentity, stage.Fingerprint);
            await destinationLease.EnsureHeldAsync(cancellationToken);
            var validation = await new RelationalMigrationValidator(validationSession, stage).ValidateAsync(cancellationToken);
            if (validation.DataRows != rows) throw new InvalidDataException("Preflight and preservation validation row counts differ.");
            await stage.WithVerifiedSourceAsync(sourceConnection, async () =>
            {
                await destinationLease.EnsureHeldAsync(cancellationToken);
                await journal.PublishAsync(validation, cancellationToken);
            }, cancellationToken);
            var ready = await probe.ProbeAsync(request.DestinationConnectionString, cancellationToken);
            if (ready.Format != PersistenceFormat.Relational) throw new InvalidDataException("The completed destination did not pass format verification.");
            progress?.Report("Conversion complete. The source database was preserved.");
            return new(stage.MigrationIdentity, ready.DatabaseName, rows, stage.DirectoryPath);
        }
        catch (OperationCanceledException)
        {
            progress?.Report("Conversion cancelled without activation. Recovery staging: " + stage.DirectoryPath);
            throw;
        }
        catch (Exception ex) when (ex is not RelationalMigrationException)
        {
            throw new RelationalMigrationException(stage.DirectoryPath, ex);
        }
    }

    private static async Task ResolveSavedDocumentBindingsAsync(RelationalSession session, LegacySourceStage stage, CancellationToken ct)
    {
        await using var connection = await session.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = """
IF NOT EXISTS(SELECT 1 FROM surf.MigrationRun r JOIN surf.StorageFormatInfo f ON f.Singleton=1
WHERE r.MigrationIdentity=@Migration AND r.SourceFingerprint=@Fingerprint AND r.ConverterVersion=1
AND r.Status='Converting' AND f.State='Migrating')
    THROW 51040,'Saved-target binding requires the pinned unpublished migration.',1;
""";
        guard.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, stage.MigrationIdentity));
        guard.Parameters.Add(RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, stage.Fingerprint, 32));
        using var cancellation = RelationalSession.CancelCommand(guard, ct);
        await guard.ExecuteNonQueryAsync(ct);
        await new State.RelationalStateStore(session).ResolveSavedDocumentBindingsAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task<bool> VerifyRecoveryAsync(RelationalSession session, LegacySourceStage stage, CancellationToken ct)
    {
        await using var connection = await session.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT r.Status FROM surf.MigrationRun r JOIN surf.SchemaMigration s ON s.SchemaVersion=1
JOIN surf.StorageFormatInfo f ON f.Singleton=1
WHERE r.MigrationIdentity=@Migration AND r.SourceFingerprint=@Fingerprint AND r.ConverterVersion=1
AND s.ScriptChecksum=@Checksum AND f.FormatIdentifier=@Format AND f.SchemaVersion=1
AND ((f.State='Migrating' AND r.Status='Converting')
 OR (f.State='Ready' AND r.Status='Complete' AND f.CompletedMigrationIdentity=@Migration));
""";
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, stage.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, stage.Fingerprint, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Checksum", SqlDbType.Binary,
            RelationalSchemaInstaller.ScriptChecksum(RelationalSchemaInstaller.ReadScripts()), 32));
        command.Parameters.Add(RelationalSession.Parameter("@Format", SqlDbType.NVarChar, PersistenceFormatProbe.FormatIdentifier, 64));
        object? status = await command.ExecuteScalarAsync(ct);
        return status is string state ? state == "Complete" : throw new InvalidDataException("Recovery source, converter, schema or migration identity does not match.");
    }
}
