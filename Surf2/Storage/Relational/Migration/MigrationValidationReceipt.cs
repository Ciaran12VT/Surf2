namespace Surf2.Storage.Relational.Migration;

/// <summary>Issued only by the completed model validator; consumed by the journal's publication gate.</summary>
internal sealed class MigrationValidationReceipt
{
    private readonly byte[] _sourceFingerprint;

    internal MigrationValidationReceipt(Guid migrationIdentity, byte[] sourceFingerprint, long dataRows,
        Guid databaseIdentity, Guid sessionEpoch)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        if (migrationIdentity == Guid.Empty || databaseIdentity == Guid.Empty || sourceFingerprint.Length != 32 || dataRows < 0)
            throw new ArgumentException("A validation receipt requires migration/destination identities, a SHA-256 source fingerprint and a nonnegative row count.");
        MigrationIdentity = migrationIdentity;
        _sourceFingerprint = sourceFingerprint.ToArray();
        DataRows = dataRows;
        DatabaseIdentity = databaseIdentity;
        SessionEpoch = sessionEpoch;
    }

    internal Guid MigrationIdentity { get; }
    internal byte[] SourceFingerprint => _sourceFingerprint.ToArray();
    internal long DataRows { get; }
    internal Guid DatabaseIdentity { get; }
    // Diagnostic only: validation and publication deliberately use different session epochs.
    internal Guid SessionEpoch { get; }
}
