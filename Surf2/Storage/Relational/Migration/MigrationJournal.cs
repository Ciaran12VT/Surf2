using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational.Migration;

public sealed class MigrationJournal(RelationalSession session, LegacySourceStage source)
{
    internal MigrationProgressReporter? Progress { get; init; }
    private long _reusedUnits;
    private long _newUnits;
    public async Task<long> UnitAsync(string documentKey, string sourcePath, string entityKind,
        long ordinal, Func<SqlConnection, SqlTransaction, CancellationToken, Task<long>> write,
        CancellationToken cancellationToken = default)
    {
        session.RejectValidationWrite();
        if (!source.StagedDocuments.TryGetValue(documentKey, out var document) || ordinal < 0)
        {
            throw new ArgumentException("A known staged source and non-negative ordinal are required.");
        }

        string identity = $"{documentKey.Length}:{documentKey}{sourcePath.Length}:{sourcePath}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        await using var connection = await session.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
IF NOT EXISTS(SELECT 1 FROM surf.MigrationRun WITH (UPDLOCK,HOLDLOCK)
    WHERE MigrationIdentity=@Migration AND SourceFingerprint=@Fingerprint AND ConverterVersion=1 AND Status='Converting')
    THROW 51003, 'Migration identity, source fingerprint or state does not match.', 1;
SELECT SourceIdentity, EntityKind, DestinationKey FROM surf.MigrationIdentityMap WITH (UPDLOCK,HOLDLOCK)
WHERE MigrationIdentity=@Migration AND SourceIdentityHash=@Hash;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, source.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, source.Fingerprint, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, hash, 32));
        using var cancellation = RelationalSession.CancelCommand(command, cancellationToken);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetString(0) != identity || reader.GetString(1) != entityKind)
                {
                    throw new InvalidDataException("A migration identity hash collision was detected.");
                }

                long key = reader.GetInt64(2);
                await reader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                _reusedUnits++;
                Progress?.Advance($"{documentKey}: {sourcePath} (reused {_reusedUnits:N0}, new {_newUnits:N0})");
                return key;
            }
        }

        long destinationKey = await write(connection, transaction, cancellationToken);
        command.CommandText = """
INSERT surf.MigrationIdentityMap(MigrationIdentity, SourceIdentityHash, SourceIdentity, EntityKind, DestinationKey)
VALUES (@Migration,@Hash,@Identity,@Kind,@Key);
UPDATE surf.MigrationCheckpoint SET RecordCount=RecordCount+1,
    LastOrdinal=CASE WHEN LastOrdinal<@Ordinal THEN @Ordinal ELSE LastOrdinal END, UpdatedAtUtc=SYSDATETIMEOFFSET()
WHERE MigrationIdentity=@Migration AND SourceDocument=@Document AND SourceHash=@SourceHash;
IF @@ROWCOUNT=0
    INSERT surf.MigrationCheckpoint(MigrationIdentity,SourceDocument,SourceHash,LastOrdinal,RecordCount,DataRowCount)
    VALUES (@Migration,@Document,@SourceHash,@Ordinal,1,0);
""";
        command.Parameters.Clear();
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, source.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, hash, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Identity", SqlDbType.NVarChar, identity, -1));
        command.Parameters.Add(RelationalSession.Parameter("@Kind", SqlDbType.NVarChar, entityKind, 128));
        command.Parameters.Add(RelationalSession.Parameter("@Key", SqlDbType.BigInt, destinationKey));
        command.Parameters.Add(RelationalSession.Parameter("@Ordinal", SqlDbType.BigInt, ordinal));
        command.Parameters.Add(RelationalSession.Parameter("@Document", SqlDbType.NVarChar, documentKey, 128));
        command.Parameters.Add(RelationalSession.Parameter("@SourceHash", SqlDbType.Binary, document.SourceHash, 32));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _newUnits++;
        Progress?.Advance($"{documentKey}: {sourcePath} (reused {_reusedUnits:N0}, new {_newUnits:N0})");
        return destinationKey;
    }

    public async Task RecordProvenanceAsync(IReadOnlyList<LegacyInventory> inventory, CancellationToken ct = default)
    {
        session.RejectValidationWrite();
        await using var connection = await session.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var document in source.StagedDocuments.Values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
INSERT surf.SourceProvenance(MigrationIdentity,DocumentKey,SourceKind,SourceSchemaVersion,OriginalTimestamp,ContentHash,ByteCount)
SELECT @Migration,@Document,@Kind,@Version,@Timestamp,@Hash,@Bytes
WHERE NOT EXISTS(SELECT 1 FROM surf.SourceProvenance WHERE MigrationIdentity=@Migration AND DocumentKey=@Document);
""";
            command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, source.MigrationIdentity));
            command.Parameters.Add(RelationalSession.Parameter("@Document", SqlDbType.NVarChar, document.Key, 128));
            command.Parameters.Add(RelationalSession.Parameter("@Kind", SqlDbType.VarChar, document.SourceKind, 16));
            command.Parameters.Add(RelationalSession.Parameter("@Version", SqlDbType.Int,
                inventory.Single(item => item.DocumentKey == document.Key).SourceSchemaVersion));
            command.Parameters.Add(RelationalSession.Parameter("@Timestamp", SqlDbType.DateTimeOffset, document.OriginalTimestamp));
            command.Parameters.Add(RelationalSession.Parameter("@Hash", SqlDbType.Binary, document.SourceHash, 32));
            command.Parameters.Add(RelationalSession.Parameter("@Bytes", SqlDbType.BigInt, document.ByteCount));
            using var cancellation = RelationalSession.CancelCommand(command, ct);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    internal async Task PublishAsync(MigrationValidationReceipt validation, CancellationToken ct = default)
    {
        session.RejectValidationWrite();
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.MigrationIdentity != source.MigrationIdentity ||
            !CryptographicOperations.FixedTimeEquals(validation.SourceFingerprint, source.Fingerprint))
            throw new InvalidOperationException("The preservation validation does not belong to this migration source.");
        await using var connection = await session.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
IF (SELECT COUNT_BIG(*) FROM surf.SourceProvenance WHERE MigrationIdentity=@Migration)<>6
    OR (SELECT COUNT_BIG(*) FROM surf.MigrationCheckpoint WHERE MigrationIdentity=@Migration)<>6
    THROW 51004, 'Not all source documents have been accounted for.', 1;
IF EXISTS(SELECT 1 FROM surf.DataSet WHERE State<>'Ready')
    OR EXISTS(SELECT 1 FROM surf.DatabaseSnapshot WHERE IsPublished=0)
    OR EXISTS(SELECT 1 FROM surf.SnapshotResourceRevision WHERE IsSealed=0)
    THROW 51005, 'A dataset or snapshot revision is incomplete.', 1;
IF COALESCE((SELECT SUM(ActualRowCount) FROM surf.DataSet),0)<>@Rows
    THROW 51006, 'Captured row preservation counts do not match.', 1;
IF EXISTS(SELECT 1 FROM surf.TextContent WHERE ByteCount<>DATALENGTH(Text)
    OR CharacterCount<>DATALENGTH(Text)/2 OR ContentHash<>HASHBYTES('SHA2_256',CONVERT(varbinary(max),Text)))
    OR EXISTS(SELECT 1 FROM surf.Asset WHERE ByteCount<>DATALENGTH(Bytes)
    OR ContentHash<>HASHBYTES('SHA2_256',Bytes))
    THROW 51007, 'Content preservation hashes do not match.', 1;
IF EXISTS(SELECT 1 FROM surf.MigrationIssue WHERE MigrationIdentity=@Migration AND Severity='Error')
    THROW 51008, 'Unresolved conversion errors prevent publication.', 1;
UPDATE surf.MigrationRun SET Status='Complete',FinishedAtUtc=SYSDATETIMEOFFSET()
WHERE MigrationIdentity=@Migration AND SourceFingerprint=@Fingerprint AND ConverterVersion=1 AND Status='Converting';
IF @@ROWCOUNT<>1 THROW 51009, 'Migration state does not permit publication.', 1;
UPDATE surf.StorageFormatInfo SET State='Ready',CompletedMigrationIdentity=@Migration
WHERE Singleton=1 AND State='Migrating' AND SchemaVersion=1 AND DatabaseIdentity=@Database;
IF @@ROWCOUNT<>1 THROW 51010, 'Storage state does not permit publication.', 1;
""";
        command.Parameters.Add(RelationalSession.Parameter("@Migration", SqlDbType.UniqueIdentifier, source.MigrationIdentity));
        command.Parameters.Add(RelationalSession.Parameter("@Fingerprint", SqlDbType.Binary, source.Fingerprint, 32));
        command.Parameters.Add(RelationalSession.Parameter("@Rows", SqlDbType.BigInt, validation.DataRows));
        command.Parameters.Add(RelationalSession.Parameter("@Database", SqlDbType.UniqueIdentifier, validation.DatabaseIdentity));
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
