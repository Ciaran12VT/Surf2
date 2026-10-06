using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Migration;

/// <summary>Imports individual staged state units; never constructs a complete legacy library.</summary>
public sealed class LegacyStateImporter
{
    private readonly RelationalSession _session;
    private readonly MigrationJournal _journal;
    private readonly LegacySourceStage _source;
    private readonly string _pastedImageDirectory;
    private readonly RelationalStateStore _state;
    private sealed record FrozenAsset(string Path, string Hash);
    private readonly Dictionary<string, FrozenAsset> _assets = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ImportedAssetPaths => _assets.ToDictionary(p => p.Key, p => p.Value.Path, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ImportedAssetHashes => _assets.ToDictionary(p => p.Key, p => p.Value.Hash, StringComparer.OrdinalIgnoreCase);

    public LegacyStateImporter(RelationalSession session, MigrationJournal journal, LegacySourceStage source, string pastedImageDirectory)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pastedImageDirectory = Path.GetFullPath(pastedImageDirectory);
        _state = new RelationalStateStore(session);
    }

    public async Task ImportAsync(CancellationToken cancellationToken = default)
    {
        _source.RequirePastedImagesFrozen();
        if (_source.SourceImageDirectory is { } directory &&
            !string.Equals(directory, _pastedImageDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The importer must use the frozen stage's original pasted-image directory.");
        await ReadAssetManifestAsync(cancellationToken);
        // Definitions must be available in the destination before a missing local PNG needs their fallback bytes.
        await ImportSingletonAsync<AppSettings>("app-settings", "Settings",
            (c,t,v,p,ct) => _state.ImportSettingsAsync(c,t,v,p,ct), cancellationToken);
        await ImportSingletonAsync<WorkspaceState>("workspace-state", "WorkspaceSession",
            (c,t,v,p,ct) => _state.ImportWorkspaceAsync(c,t,v,p,ct), cancellationToken);

        string scopeFile = FilePath("scope-library");
        var selection = await LegacyProjectionReader.HeaderAsync<ScopeLibrary>(scopeFile, 0, ["Scopes"], cancellationToken);
        if (selection.SchemaVersion != 1) throw new InvalidDataException("The staged scope library schema version is unsupported.");
        await _journal.UnitAsync("scope-library", "$", "ScopeSelection", 0, async (c,t,ct) =>
            (await _state.ImportScopeSelectionAsync(c,t,selection.SchemaVersion,selection.LastActiveScopeId,
                Publication("scope-library", "$"),ct)).Key, cancellationToken);
        await ImportArrayAsync<Scope>("scope-library", "Scopes", "Scope",
            (c,t,v,o,p,ct) => _state.ImportScopeAsync(c,t,v,o,p,ct), cancellationToken);

        // Headers are separately checkpointed even when their authoritative arrays are empty.
        await HeaderAsync<DiagramLibrary>("diagram-library", "Diagrams", "DiagramLibraryHeader", cancellationToken);
        await ImportArrayAsync<DiagramDocument>("diagram-library", "Diagrams", "Diagram",
            async (c,t,v,o,p,ct) =>
            {
                var prepared = await PrepareAndFreezeAsync(c,t,v,ct);
                return await _state.ImportDiagramAsync(c,t,prepared,o,p,ct);
            }, cancellationToken);
        await HeaderAsync<WorkbenchLibrary>("workbench-library", "Workbenches", "WorkbenchLibraryHeader", cancellationToken);
        await ImportArrayAsync<WorkbenchState>("workbench-library", "Workbenches", "Workbench",
            async (c,t,v,o,p,ct) =>
            {
                IReadOnlyDictionary<int, byte[]> pasted = new Dictionary<int, byte[]>();
                IReadOnlyDictionary<int, PastedImageFallback>? fallbacks = null;
                if (v.ActiveDiagramSnapshot != null)
                {
                    var prepared = await PrepareAndFreezeAsync(c,t,v.ActiveDiagramSnapshot,ct);
                    v.ActiveDiagramSnapshot = prepared.Document;
                    pasted = prepared.PastedImages;
                    fallbacks = prepared.PastedImageFallbacks;
                }
                return await _state.ImportWorkbenchAsync(c,t,new WorkbenchAggregate(v,pasted,fallbacks),o,p,ct);
            }, cancellationToken);
        // The coordinator runs this importer after the complete snapshot phase.
        await ResolveScopeTargetsAsync(cancellationToken);
    }

    private async Task HeaderAsync<T>(string document, string array, string kind, CancellationToken ct) where T : class
    {
        _ = await LegacyProjectionReader.HeaderAsync<T>(FilePath(document), 0, [array], ct);
        await _journal.UnitAsync(document, "$", kind, 0, (_,_,_) => Task.FromResult(1L), ct);
    }

    private async Task ImportSingletonAsync<T>(string document, string kind,
        Func<SqlConnection, SqlTransaction, T, Guid, CancellationToken, Task<StateToken>> write, CancellationToken ct) where T : class
    {
        await _journal.UnitAsync(document, "$", kind, 0, async (connection, transaction, cancellation) =>
        {
            await using var cursor = _source.OpenDocument(document);
            if (!await cursor.MoveNextAsync(cancellation)) throw new InvalidDataException($"The staged {document} aggregate is missing.");
            T value = LegacySchemaInspector.Deserialize<T>(await cursor.ReadValueAsync(cancellation));
            if (await cursor.MoveNextAsync(cancellation)) throw new InvalidDataException($"The staged {document} has extra root values.");
            var token = await write(connection, transaction, value, Publication(document, "$"), cancellation);
            await WarningsAsync(connection, transaction, document, 0, token.ImportWarnings, cancellation);
            return token.Key;
        }, ct);
    }

    private async Task ImportArrayAsync<T>(string document, string array, string kind,
        Func<SqlConnection, SqlTransaction, T, long, Guid, CancellationToken, Task<StateToken>> write, CancellationToken ct) where T : class
    {
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath(document), 0, array, async (ordinal, cursor, cancellation) =>
        {
            // Consume precisely this one bounded aggregate even when its checkpoint is already committed.
            T value = LegacySchemaInspector.Deserialize<T>(await cursor.ReadValueAsync(cancellation));
            string path = ArrayPath(array, ordinal);
            await _journal.UnitAsync(document, path, kind, ordinal, async (connection, transaction, unitCancellation) =>
            {
                var token = await write(connection, transaction, value, ordinal, Publication(document, path), unitCancellation);
                await WarningsAsync(connection, transaction, document, ordinal, token.ImportWarnings, unitCancellation);
                return token.Key;
            }, cancellation);
        }, ct);
    }

    /// <summary>Journaled scope, diagram and workbench reference pass after snapshot and selected State imports, before publication.</summary>
    public async Task ResolveScopeTargetsAsync(CancellationToken cancellationToken = default)
    {
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath("scope-library"), 0, "Scopes", async (ordinal, cursor, ct) =>
        {
            Scope scope = LegacySchemaInspector.Deserialize<Scope>(await cursor.ReadValueAsync(ct));
            string path = ArrayPath("Scopes", ordinal);
            await _journal.UnitAsync("scope-library", path + "/targets", "ScopeTargetResolution", ordinal, async (connection, transaction, cancellation) =>
            {
                long key = await MappedOwnerKeyAsync(connection, transaction, "scope-library", path, "Scope", cancellation);
                StateToken expected = await OwnerTokenAsync(connection, transaction, "Scope", "ScopeKey", key, cancellation);
                var targets = new List<ScopeResourceTarget>();
                for (int i = 0; i < scope.Resources.Count; i++)
                {
                    var resource = scope.Resources[i];
                    if (resource.Kind is not (ResourceKind.DatabaseSnapshot or ResourceKind.Diagram)) continue;
                    var matches = await TargetKeysAsync(connection, transaction, resource.Kind, resource.Path, cancellation);
                    if (matches.Count == 1)
                        targets.Add(new(i, resource.Kind == ResourceKind.DatabaseSnapshot ? matches[0] : null,
                            resource.Kind == ResourceKind.Diagram ? matches[0] : null));
                    else
                        await IssueAsync(connection, transaction, "scope-library", ordinal,
                            matches.Count == 0 ? "UnresolvedScopeTarget" : "AmbiguousScopeTarget",
                            $"Resource ordinal {i.ToString(CultureInfo.InvariantCulture)} retains its original locator without an unambiguous typed target.", cancellation);
                }
                if (targets.Count != 0)
                    await _state.ResolveScopeResourceTargetsAsync(connection, transaction, expected, targets,
                        Publication("scope-library", path + "/targets"), cancellation);
                return key;
            }, ct);
        }, cancellationToken);
        await ResolveStateReferencesAsync(cancellationToken);
    }

    /// <summary>Journaled selected-owner pass after all scopes, diagrams and workbenches exist.</summary>
    public async Task ResolveStateReferencesAsync(CancellationToken cancellationToken = default)
    {
        await ResolveArrayReferencesAsync("diagram-library", "Diagrams", "Diagram", "DiagramKey",
            (c,t,v,p,ct) => _state.ResolveDiagramReferencesAsync(c,t,v,p,ct), cancellationToken);
        await ResolveArrayReferencesAsync("workbench-library", "Workbenches", "Workbench", "WorkbenchKey",
            (c,t,v,p,ct) => _state.ResolveWorkbenchReferencesAsync(c,t,v,p,ct), cancellationToken);
    }

    private async Task ResolveArrayReferencesAsync(string document, string array, string kind, string keyColumn,
        Func<SqlConnection, SqlTransaction, StateToken, Guid, CancellationToken, Task<StateToken>> resolve, CancellationToken ct)
    {
        await LegacyProjectionReader.ArrayPropertyAsync(FilePath(document), 0, array, async (ordinal, cursor, cancellation) =>
        {
            // This pass needs only the deterministic source ordinal, not another aggregate hydration.
            await cursor.SkipValueAsync(cancellation);
            string sourcePath = ArrayPath(array, ordinal);
            string unitPath = sourcePath + "/references";
            await _journal.UnitAsync(document, unitPath, kind + "ReferenceResolution", ordinal, async (connection, transaction, token) =>
            {
                long key = await MappedOwnerKeyAsync(connection, transaction, document, sourcePath, kind, token);
                StateToken expected = await OwnerTokenAsync(connection, transaction, kind, keyColumn, key, token);
                return (await resolve(connection, transaction, expected, Publication(document, unitPath), token)).Key;
            }, cancellation);
        }, ct);
    }

    private async Task<long> MappedOwnerKeyAsync(SqlConnection connection, SqlTransaction transaction,
        string document, string path, string kind, CancellationToken ct)
    {
        string identity = $"{document.Length}:{document}{path.Length}:{path}";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
SELECT SourceIdentity, EntityKind, DestinationKey FROM surf.MigrationIdentityMap
WHERE MigrationIdentity=@Migration AND SourceIdentityHash=@Hash;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, _source.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, SHA256.HashData(Encoding.UTF8.GetBytes(identity)), 32));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.GetString(0) != identity || reader.GetString(1) != kind)
            throw new InvalidDataException("Import the selected owner before resolving its targets; the migration mapping is missing or inconsistent.");
        return reader.GetInt64(2);
    }

    private async Task<StateToken> OwnerTokenAsync(SqlConnection connection, SqlTransaction transaction,
        string table, string keyColumn, long key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT Version, PublicationId FROM surf.[{table}] WHERE [{keyColumn}]=@Key AND ProfileKey=1;";
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, key));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidDataException("A committed state migration mapping has no selected owner.");
        return new(key, _session.Epoch, (byte[])reader.GetValue(0), reader.GetGuid(1));
    }

    private static async Task<IReadOnlyList<long>> TargetKeysAsync(SqlConnection connection, SqlTransaction transaction,
        ResourceKind kind, string originalId, CancellationToken ct)
    {
        var matches = new List<long>(2);
        long after = 0;
        while (matches.Count < 2)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = kind == ResourceKind.DatabaseSnapshot ? """
SELECT TOP (256) SnapshotKey, DATALENGTH(OriginalSnapshotId), OriginalSnapshotId FROM surf.DatabaseSnapshot
WHERE UserKey=1 AND IsPublished=1 AND SnapshotKey>@After ORDER BY SnapshotKey;
""" : """
SELECT TOP (256) DiagramKey, DATALENGTH(DiagramId), DiagramId FROM surf.Diagram
WHERE ProfileKey=1 AND CurrentRevisionKey IS NOT NULL AND DiagramKey>@After ORDER BY DiagramKey;
""";
            command.Parameters.Add(RelationalSession.Parameter("@After", SqlDbType.BigInt, after));
            using var cancel = RelationalSession.CancelCommand(command, ct);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
            int rows = 0;
            while (await reader.ReadAsync(ct))
            {
                rows++;
                after = reader.GetInt64(0);
                if (reader.GetInt64(1) > 16L * 1024 * 1024) throw new InvalidDataException("A target identifier exceeds the migration lookup budget.");
                using var text = reader.GetTextReader(2);
                string id = await text.ReadToEndAsync(ct);
                if (string.Equals(id, originalId, StringComparison.OrdinalIgnoreCase)) matches.Add(after);
                if (matches.Count == 2) break;
            }
            if (rows < 256) break;
        }
        return matches;
    }

    private async Task WarningsAsync(SqlConnection connection, SqlTransaction transaction, string document, long ordinal,
        IReadOnlyList<StateImportWarning> warnings, CancellationToken ct)
    {
        foreach (var warning in warnings)
            await IssueAsync(connection, transaction, document, ordinal, warning.Code,
                $"Object ordinal {warning.ObjectOrdinal.ToString(CultureInfo.InvariantCulture)} retains its filename and uses preserved " +
                (warning.Code == "MissingPastedImageUsedInline" ? "inline" : "definition") + " bytes because its local pasted PNG was missing.", ct);
    }

    private async Task<DiagramState> PrepareAndFreezeAsync(SqlConnection connection, SqlTransaction transaction,
        DiagramDocument diagram, CancellationToken ct)
    {
        return await _state.PrepareDiagramImportAsync(connection, transaction, diagram, _pastedImageDirectory,
            ReadFrozenPngAsync, ct);
    }

    private Task<byte[]?> ReadFrozenPngAsync(string original, CancellationToken ct) => _source.ReadFrozenPngAsync(original, ct);

    private Task ReadAssetManifestAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _assets.Clear();
        foreach (var asset in _source.StagedAssets)
        {
            _assets.Add(asset.OriginalPath, new(asset.FilePath, Convert.ToHexString(asset.Hash).ToLowerInvariant()));
        }
        return Task.CompletedTask;
    }

    private async Task IssueAsync(SqlConnection connection, SqlTransaction transaction, string document, long ordinal,
        string code, string message, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
INSERT surf.MigrationIssue(MigrationIdentity, Severity, SourceDocument, SourceOrdinal, IssueCode, Message)
VALUES (@Migration, 'Warning', @Document, @Ordinal, @Code, @Message);
""";
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, _source.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Document", SqlDbType.NVarChar, document, 128));
        command.Parameters.Add(RelationalSession.Parameter("@Ordinal", SqlDbType.BigInt, ordinal));
        command.Parameters.Add(RelationalSession.Parameter("@Code", SqlDbType.NVarChar, code, 128));
        command.Parameters.Add(RelationalSession.Parameter("@Message", SqlDbType.NVarChar, message, 2048));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    private string FilePath(string document) => _source.StagedDocuments.TryGetValue(document, out var staged)
        ? staged.FilePath : throw new InvalidDataException($"The staged {document} document is missing.");
    private static string ArrayPath(string array, long ordinal) => $"$.{array}[{ordinal.ToString(CultureInfo.InvariantCulture)}]";
    private Guid Publication(string document, string path) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{_source.MigrationIdentity:N}:{document}:{path}")).AsSpan(0, 16));
}
