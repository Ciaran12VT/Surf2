using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Surf2.Storage;
using Surf2.Storage.Relational;
using Surf2.Storage.Relational.Migration;
using Surf2.Storage.Relational.Packages;

namespace Surf2.Services;

public enum PersistencePackageKind { Legacy, Relational }
public sealed record PersistencePackageInspection(PersistencePackageKind Kind, string ManifestSha256,
    int? PackageVersion=null,int? DatabaseSchemaVersion=null,int? ExporterVersion=null,int? RowEncodingVersion=null);
public sealed record PersistenceTransferResult(string DatabaseName, Guid DatabaseIdentity, long CapturedRows,
    SqlServerConnectionOptions Destination, string? RecoveryDirectory);

public sealed class RelationalPersistenceTransfer
{
    public static async Task<PersistencePackageInspection> InspectPackageAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("The package has too many entries.");
        var manifests = archive.Entries.Where(e => string.Equals(e.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1 || manifests[0].FullName != "manifest.json" || manifests[0].Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The package must have one bounded manifest.");
        await using var stream = manifests[0].Open();
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > 16 * 1024 * 1024) throw new InvalidDataException("The package manifest exceeds its budget.");
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length != manifests[0].Length) throw new InvalidDataException("The manifest length changed.");
        bytes.Position = 0;
        using var json = await JsonDocument.ParseAsync(bytes, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The manifest is not an object.");
        if(json.RootElement.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=json.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("The package manifest contains duplicate fields.");
        PersistencePackageKind kind;
        int? packageVersion=null,schemaVersion=null,exporterVersion=null,rowVersion=null;
        if (json.RootElement.TryGetProperty("PackageIdentifier", out var identifier) &&
            identifier.GetString() == RelationalPackageFormat.Identifier)
        {
            int Number(string name)=>json.RootElement.TryGetProperty(name,out var value)&&value.TryGetInt32(out var result)?result:throw new InvalidDataException("Missing package version field: "+name);
            packageVersion=Number("PackageVersion");schemaVersion=Number("DatabaseSchemaVersion");
            exporterVersion=Number("ExporterVersion");rowVersion=Number("RowEncodingVersion");
            if(!((packageVersion==2&&schemaVersion is 1 or 2)||(packageVersion==3&&schemaVersion==3)) ||
                exporterVersion!=1||rowVersion!=1 || Number("MinimumReaderVersion")!=schemaVersion ||Number("MinimumWriterVersion")!=schemaVersion ||
                !json.RootElement.TryGetProperty("DatabaseFormatIdentifier",out var databaseFormat)||databaseFormat.GetString()!=PersistenceFormatProbe.FormatIdentifier)
                throw new InvalidDataException("The package/schema/encoding version combination is not supported.");
            kind = PersistencePackageKind.Relational;
        }
        else if (json.RootElement.TryGetProperty("FormatVersion", out var legacy) && legacy.GetInt32() == 1 &&
            !json.RootElement.TryGetProperty("PackageIdentifier", out _))
            kind = PersistencePackageKind.Legacy;
        else throw new InvalidDataException("This package format is not supported.");
        return new(kind, Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))),packageVersion,schemaVersion,exporterVersion,rowVersion);
    }

    public async Task<PersistenceTransferResult> ImportIntoNewDatabaseAsync(string packagePath,
        SqlServerConnectionOptions current, string destinationName, string appDataDirectory,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationName);
        if (destinationName.Length > 128 || destinationName.Any(char.IsControl) ||
            string.Equals(current.DatabaseName, destinationName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a valid, different destination database name.", nameof(destinationName));
        // Keep the selected archive immutable across inspection, target creation and either importer.
        await using var sourceLease = new FileStream(Path.GetFullPath(packagePath), FileMode.Open, FileAccess.Read,
            FileShare.Read, 1, FileOptions.Asynchronous);
        var inspection = await InspectPackageAsync(packagePath, ct);
        var builder = new SqlConnectionStringBuilder(current.ConnectionString) { InitialCatalog = destinationName };
        var destination = SqlServerConnectionOptions.FromConnectionString(builder.ConnectionString);
        // This explicit operation refuses an existing database, even if it is empty.
        await new RelationalSchemaInstaller().CreateEmptyDatabaseAsync(destination.ConnectionString, ct);
        if (inspection.Kind == PersistencePackageKind.Relational)
        {
            progress?.Report("Importing into the new database...");
            var imported = await new RelationalPackageImporter(destination.ConnectionString).ImportAsync(packagePath,
                new() { ExpectedManifestSha256 = inspection.ManifestSha256 }, ct);
            return new(destinationName, imported.DatabaseIdentity, imported.CapturedRows, destination, null);
        }
        var migration = await new RelationalMigrator().ConvertAsync(new(current.ConnectionString,
            destination.ConnectionString, appDataDirectory, Path.Combine(appDataDirectory, "Migrations"),
            SourcePackagePath: Path.GetFullPath(packagePath)), progress, ct);
        var marker = await new PersistenceFormatProbe().ProbeAsync(destination.ConnectionString, ct);
        if (marker.Format != PersistenceFormat.Relational || !marker.DatabaseIdentity.HasValue)
            throw new InvalidOperationException("The imported legacy package did not produce a validated destination.");
        return new(destinationName, marker.DatabaseIdentity.Value, migration.CapturedRows, destination, migration.StageDirectory);
    }
}
