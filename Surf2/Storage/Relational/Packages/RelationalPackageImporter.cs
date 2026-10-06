using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Microsoft.Data.SqlClient;
using Surf2.Storage.Relational.Migration;

namespace Surf2.Storage.Relational.Packages;

/// <summary>
/// Fresh-target-only import. Every batch may commit while Migrating, but Ready is
/// published only after full validation. Failure requires another Empty target,
/// not an unverified replay of a partially imported graph. No activation occurs.
/// </summary>
public sealed partial class RelationalPackageImporter
{
    private readonly string _destination;
    private readonly RelationalSession _session;
    private readonly RelationalPackageLimits _limits;

    public RelationalPackageImporter(string destinationConnectionString, RelationalPackageLimits? limits = null)
    {
        _destination = SqlServerConnectionOptions.FromConnectionString(destinationConnectionString).ConnectionString;
        _session = new(_destination);
        _limits = limits ?? new(); _limits.Validate();
    }

    public async Task<RelationalPackageImportResult> ImportAsync(string packagePath,
        RelationalPackageImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var settings = options ?? new(); settings.Validate();
        var ct = cancellationToken; ct.ThrowIfCancellationRequested();
        _session.RejectValidationWrite();
        Guid import = settings.ImportIdentity ?? Guid.NewGuid();
        await using var file = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read, FileShare.Read,
            _limits.ChunkBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await PreflightZipAsync(file, ct);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var package = await ReadPackageAsync(archive, settings, ct);
        await VerifyLocalFilesAsync(package, ct);
        byte[] fingerprint = PackageRowDecoding.Hash(package.ManifestHash);
        await using var lease = await MigrationTargetLease.AcquireAsync(_session, ct);
        if ((await new PersistenceFormatProbe().ProbeAsync(_destination, ct)).Format != PersistenceFormat.Empty)
            throw new RelationalPackageImportException("Package import requires a verified Empty destination. A failed/cancelled import is not resumable; select another fresh destination.");
        await new RelationalSchemaInstaller().InitializePackageDestinationAsync(_destination, import, fingerprint, ct);
        await lease.EnsureHeldAsync(ct);
        await using var connection = await _session.OpenAsync(ct);
        Guid database = await RequireImportAsync(connection, null, import, fingerprint, null, ct);
        if (database == package.Manifest.SourceDatabaseIdentity) throw PackageRowDecoding.Bad("Destination must have an independent database identity");
        var known = new List<KnownTable>();
        // Check every fixed-table definition before the first imported value or deferred constraint.
        foreach (var spec in PackageTableCatalogue.Authoritative)
        {
            var table = await ReadKnownAsync(connection, null, spec, ct);
            CompareMetadata(package.Tables[spec.Key].Table, table.Metadata); known.Add(table);
        }
        await ConstraintsAsync(connection, null, known, enable: false, ct);
        long rows = 0;
        foreach (var table in known)
            rows = checked(rows + await LoadTableAsync(connection, table, package.Tables[table.Spec.Key], package,
                settings, import, fingerprint, database, ct));
        var captures = await CreateCaptureTablesAsync(connection, package, ct);
        await ConstraintsAsync(connection, null, captures, enable: false, ct);
        foreach (var table in captures)
            rows = checked(rows + await LoadTableAsync(connection, table, package.Tables[table.Spec.Key], package,
                settings, import, fingerprint, database, ct));
        known.AddRange(captures);
        await lease.EnsureHeldAsync(ct);
        long capturedRows = await ValidateCaptureModelsAsync(connection, captures, import, fingerprint, database, ct);
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            await RequireImportAsync(connection, transaction, import, fingerprint, database, ct);
            await ConstraintsAsync(connection, transaction, known, enable: true, ct);
            foreach (var table in known)
            {
                CompareMetadata(package.Tables[table.Spec.Key].Table, (await ReadKnownAsync(connection, transaction, table.Spec, ct)).Metadata);
                await ValidateStoredTableAsync(connection, transaction, table, package.Tables[table.Spec.Key], package, settings, ct);
            }
            await ValidateDatabaseAsync(connection, transaction, known, captures, capturedRows, ct);
            await lease.EnsureHeldAsync(ct);
            await PublishAsync(connection, transaction, package, import, fingerprint, database, ct);
            await transaction.CommitAsync(ct);
        }
        // Cancellation after this commit must not report an unpublished destination.
        return new(import, database, package.Manifest.SourceDatabaseIdentity, package.ManifestHash, rows,
            capturedRows, package.Manifest.LocalFiles.Count, package.Bytes);
    }

    private async Task<long> LoadTableAsync(SqlConnection connection, KnownTable table, RelationalPackageTableStream stream,
        PackageInput package, RelationalPackageImportOptions options, Guid import, byte[] fingerprint, Guid database, CancellationToken ct)
    {
        var columns = table.StreamColumns;
        long fixedBytes = BulkFixedBytes(columns.Length, options.BatchRows);
        long rowOverhead = BulkRowOverhead(columns.Length);
        long rowBudget = BulkRowPayloadBudget(columns.Length, options);
        string columnNames = string.Join(",", columns.Select(c => PackageTableCatalogue.Quote(c.Name)));
        string projection = string.Join(",", columns.Select(c => PackageRowEncoding.IsChunked(c.Encoding!)
            ? "CONVERT(" + TypeSql(c, staging: false) + "," + PackageTableCatalogue.Quote(c.Name) + ")"
            : PackageTableCatalogue.Quote(c.Name)));
        await ExecuteAsync(connection, null, "CREATE TABLE #PackageRows(" + string.Join(",", columns.Select(c =>
            PackageTableCatalogue.Quote(c.Name) + " " + TypeSql(c, staging: true) + (c.Nullable ? " NULL" : " NOT NULL"))) + ");", ct);
        try
        {
            using var batch = new DataTable { Locale = CultureInfo.InvariantCulture, MinimumCapacity = options.BatchRows };
            foreach (var column in columns) batch.Columns.Add(column.Name, ClrType(column));
            long batchBytes = fixedBytes, count = 0;
            long[]? previous = null;
            if (table.Spec.Name == "UserProfile" && stream.RowCount != 1) throw PackageRowDecoding.Bad("Exactly one profile must be supplied");
            await using var input = package.Entries[stream.Entry].Open();
            using var verified = new PackageEntryReadStream(input, stream.ByteCount, PackageRowDecoding.Hash(stream.Sha256));
            var decoder = new PackageRowDecoding(verified, _limits, rowBudget, async (payloadBytes, token) =>
            {
                token.ThrowIfCancellationRequested();
                long retained = checked(payloadBytes + rowOverhead);
                if (retained > options.BatchBytes - batchBytes && batch.Rows.Count > 0) await FlushAsync();
                if (retained > options.BatchBytes - batchBytes)
                    throw new RelationalPackageLimitException("One row plus fixed bulk storage exceeds BatchBytes; the installed destination remains Migrating.");
            });
            while (true)
            {
                object[]? row = await decoder.ReadAsync(columns, ct);
                if (row == null) break;
                if (count >= stream.RowCount) throw PackageRowDecoding.Bad("Extra rows beyond the manifest row count");
                PackageRowDecoding.OrderedKey(columns, table.Metadata.PrimaryKey, row, ref previous);
                if (table.Spec.Name == "UserProfile" && ((long)row[0] != 1 || (Guid)row[1] == Guid.Empty))
                    throw PackageRowDecoding.Bad("The sole user profile must be key 1 with a nonempty identity");
                long retained = checked(decoder.RowBytes + rowOverhead);
                if (retained > options.BatchBytes - batchBytes)
                    throw new RelationalPackageLimitException("The decoded bulk row exceeds its pre-allocation reservation.");
                batch.Rows.Add(row); batchBytes = checked(batchBytes + retained); count++;
                row = null;
                if (batch.Rows.Count >= options.BatchRows || batchBytes >= options.BatchBytes) await FlushAsync();
            }
            if (count != stream.RowCount) throw PackageRowDecoding.Bad("Fewer rows than the manifest row count");
            verified.Finish();
            if (batch.Rows.Count > 0) await FlushAsync();
            await ReseedAsync(connection, table, stream.Table, ct);
            return count;

            async Task FlushAsync()
            {
                ct.ThrowIfCancellationRequested();
                if (batch.Rows.Count == 0 || batch.Rows.Count > options.BatchRows || batchBytes > options.BatchBytes)
                    throw new RelationalPackageLimitException("The package bulk batch exceeds its configured row/byte budget.");
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
                await RequireImportAsync(connection, transaction, import, fingerprint, database, ct);
                using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
                { DestinationTableName = "#PackageRows", BatchSize = batch.Rows.Count, BulkCopyTimeout = _limits.CommandTimeoutSeconds, EnableStreaming = true })
                {
                    foreach (var column in columns) bulk.ColumnMappings.Add(column.Name, column.Name);
                    await bulk.WriteToServerAsync(batch, ct);
                }
                string write;
                if (table.Spec.Name == "UserProfile")
                    write = "UPDATE p SET ProfileIdentity=s.ProfileIdentity,LastActiveScopeId=CONVERT(nvarchar(max),s.LastActiveScopeId) FROM surf.UserProfile p JOIN #PackageRows s ON s.ProfileKey=p.ProfileKey;";
                else
                {
                    bool identity = columns.Any(c => c.Identity);
                    write = (identity ? "SET IDENTITY_INSERT " + table.Spec.Qualified + " ON;\n" : "") +
                        "INSERT " + table.Spec.Qualified + "(" + columnNames + ") SELECT " + projection + " FROM #PackageRows;\n" +
                        (identity ? "SET IDENTITY_INSERT " + table.Spec.Qualified + " OFF;\n" : "");
                }
                await ExecuteAsync(connection, transaction, write + "TRUNCATE TABLE #PackageRows;", ct);
                await transaction.CommitAsync(ct);
                batch.Clear(); batchBytes = fixedBytes;
            }
        }
        finally
        {
            // Only the connection-local staging table is removed; the unpublished target remains reviewable.
            if (connection.State == ConnectionState.Open)
            {
                try { await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS #PackageRows;", CancellationToken.None); }
                catch (Exception error) when (error is SqlException or InvalidOperationException) { }
            }
        }
    }

    internal static long BulkFixedBytes(int columns, int rows)
    {
        if (columns is < 1 or > 1024 || rows is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(columns));
        int capacity = 128;
        while (capacity < rows) capacity *= 2;
        // Reserve rounded storage arrays, per-column schema objects and record-manager arrays.
        // 64 bytes/slot covers the widest supported SqlType storage without assuming reference-only columns.
        return checked(4096L + columns * (2048L + capacity * 64L) + capacity * 32L);
    }

    internal static long BulkRowOverhead(int columns) => checked(4096L + columns * 256L);

    internal static long BulkRowPayloadBudget(int columns, RelationalPackageImportOptions options)
    {
        long remaining = options.BatchBytes - BulkFixedBytes(columns, options.BatchRows) - BulkRowOverhead(columns);
        if (remaining < 0) throw new RelationalPackageLimitException("BatchBytes cannot fit this table's fixed schema/storage and one row. The installed destination remains Migrating.");
        return remaining;
    }

    private async Task ReseedAsync(SqlConnection connection, KnownTable table, RelationalPackageTable source, CancellationToken ct)
    {
        foreach (var column in table.Metadata.Columns.Where(c => c.Identity))
        {
            await using var command = Command(connection, null, "SELECT MAX(" + PackageTableCatalogue.Quote(column.Name) + ") FROM " + table.Spec.Qualified + ";");
            using var cancel = RelationalSession.CancelCommand(command, ct);
            object? maximum = await command.ExecuteScalarAsync(ct);
            string? observedCounter = source.Columns.Single(c => c.Name == column.Name).IdentityLastValue;
            long? counter = observedCounter == null ? null : PackageRowDecoding.Integer(observedCounter, long.MinValue, long.MaxValue);
            if (maximum is long restored) counter = Math.Max(counter ?? restored, restored);
            if (counter.HasValue)
                await ExecuteAsync(connection, null, "DBCC CHECKIDENT ('" + table.Spec.Qualified + "', RESEED, " + counter.Value.ToString(CultureInfo.InvariantCulture) + ") WITH NO_INFOMSGS;", ct);
        }
    }

    private static Type ClrType(RelationalPackageColumn c) => PackageRowEncoding.IsChunked(c.Encoding!) ? typeof(byte[]) : c.SqlType switch
    {
        "bigint" => typeof(long), "int" => typeof(int), "smallint" => typeof(short), "tinyint" => typeof(byte), "bit" => typeof(bool),
        "decimal" or "numeric" => typeof(SqlDecimal), "money" or "smallmoney" => typeof(SqlMoney),
        "float" when c.Encoding == "float64-bits-v1" => typeof(double), "float" or "real" => typeof(float),
        "uniqueidentifier" => typeof(Guid), "date" or "datetime" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset), "time" => typeof(TimeSpan), _ => throw PackageRowDecoding.Bad("Unsupported installed scalar")
    };

    private async Task<Guid> RequireImportAsync(SqlConnection connection, SqlTransaction? transaction, Guid import,
        byte[] fingerprint, Guid? expectedDatabase, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
SELECT f.DatabaseIdentity FROM surf.StorageFormatInfo f JOIN surf.MigrationRun r ON r.MigrationIdentity=@Import
WHERE f.Singleton=1 AND f.FormatIdentifier=N'Surf2.Relational' AND f.State='Migrating' AND f.SchemaVersion=1
AND f.MinimumReaderVersion=1 AND f.MinimumWriterVersion=1 AND r.SourceFingerprint=@Hash
AND r.ConverterVersion=1 AND r.Status='Converting';
""", RelationalSession.Parameter("@Import", SqlDbType.UniqueIdentifier, import), RelationalSession.Parameter("@Hash", SqlDbType.Binary, fingerprint, 32));
        using var cancel = RelationalSession.CancelCommand(command, ct);
        if (await command.ExecuteScalarAsync(ct) is not Guid database || database == Guid.Empty ||
            (expectedDatabase.HasValue && database != expectedDatabase.Value))
            throw new RelationalPackageImportException("The pinned unpublished package destination changed. Select another fresh destination.");
        return database;
    }

    private SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, params SqlParameter[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandTimeout = _limits.CommandTimeoutSeconds; command.CommandText = sql; command.Parameters.AddRange(parameters);
        return command;
    }

    private async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var command = Command(connection, transaction, sql, parameters);
        using var cancel = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct);
    }
}
