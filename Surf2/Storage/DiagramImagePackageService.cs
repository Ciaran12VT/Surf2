using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Surf2.Models;

namespace Surf2.Storage;

public sealed class DiagramImagePackageService
{
    private const int CurrentFormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const string ImagesRoot = "images/";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public async Task<DiagramImagePackageExportResult> ExportAsync(
        string packagePath,
        IEnumerable<DiagramImageDefinition> images,
        CancellationToken cancellationToken = default)
    {
        List<DiagramImagePackageImage> packageImages = [];
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((DiagramImageDefinition image, int index) in images.Select((image, index) => (image, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(image.ImageDataBase64))
            {
                throw new InvalidDataException($"Diagram image '{image.Name}' does not contain image data.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.ImageDataBase64);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException($"Diagram image '{image.Name}' has invalid image data.", ex);
            }

            string extension = GetImageExtension(image.OriginalFileName, bytes);
            string entryName = CreateImageEntryName(image.Name, index, extension, usedEntryNames);

            packageImages.Add(new DiagramImagePackageImage
            {
                Id = image.Id,
                Name = image.Name,
                Regex = image.Regex,
                MatchTarget = DiagramImageDefinition.NormalizeMatchTarget(image.MatchTarget),
                NameRegex = image.NameRegex,
                ContentRegex = image.ContentRegex,
                ResourceTypeFilter = DiagramImageDefinition.NormalizeResourceTypeFilter(image.ResourceTypeFilter),
                SortOrder = image.SortOrder > 0 ? image.SortOrder : index + 1,
                OriginalFileName = image.OriginalFileName,
                FileName = entryName,
                ContentType = GetContentType(extension),
                ImageBytes = bytes
            });
        }

        if (packageImages.Count == 0)
        {
            throw new InvalidOperationException("There are no diagram images to export.");
        }

        string packageFullPath = Path.GetFullPath(packagePath);
        string? packageDirectory = Path.GetDirectoryName(packageFullPath);
        if (!string.IsNullOrWhiteSpace(packageDirectory))
        {
            Directory.CreateDirectory(packageDirectory);
        }

        if (File.Exists(packageFullPath))
        {
            File.Delete(packageFullPath);
        }

        await using FileStream packageStream = File.Create(packageFullPath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create);

        foreach (DiagramImagePackageImage image in packageImages)
        {
            ZipArchiveEntry entry = archive.CreateEntry(image.FileName, CompressionLevel.Optimal);
            await using Stream entryStream = entry.Open();
            await entryStream.WriteAsync(image.ImageBytes, cancellationToken);
        }

        var manifest = new DiagramImagePackageManifest
        {
            FormatVersion = CurrentFormatVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            AppName = "Surf2",
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            Images = packageImages
                .Select(image => new DiagramImagePackageManifestImage
                {
                    Id = image.Id,
                    Name = image.Name,
                    Regex = image.Regex,
                    MatchTarget = image.MatchTarget,
                    NameRegex = image.NameRegex,
                    ContentRegex = image.ContentRegex,
                    ResourceTypeFilter = image.ResourceTypeFilter,
                    SortOrder = image.SortOrder,
                    OriginalFileName = image.OriginalFileName,
                    FileName = image.FileName,
                    ContentType = image.ContentType
                })
                .ToList()
        };

        ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using (Stream manifestStream = manifestEntry.Open())
        {
            await JsonSerializer.SerializeAsync(manifestStream, manifest, SerializerOptions, cancellationToken);
        }

        return new DiagramImagePackageExportResult(packageImages.Count);
    }

    public async Task<DiagramImagePackageImportResult> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        await using FileStream packageStream = File.OpenRead(packagePath);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);

        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("The selected file is not a Surf2 image package.");

        DiagramImagePackageManifest manifest;
        await using (Stream manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DiagramImagePackageManifest>(
                    manifestStream,
                    SerializerOptions,
                    cancellationToken)
                ?? throw new InvalidDataException("The selected image package manifest is empty.");
        }

        if (manifest.FormatVersion < 1 || manifest.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException("The selected image package uses an unsupported format version.");
        }

        manifest.Images ??= [];
        if (manifest.Images.Count == 0)
        {
            throw new InvalidDataException("The selected image package does not contain any images.");
        }

        var images = new List<DiagramImageDefinition>();
        foreach (DiagramImagePackageManifestImage manifestImage in manifest.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSafeImageEntryName(manifestImage.FileName))
            {
                throw new InvalidDataException($"Image entry '{manifestImage.FileName}' is not valid.");
            }

            ZipArchiveEntry imageEntry = archive.GetEntry(manifestImage.FileName)
                ?? throw new InvalidDataException($"Image entry '{manifestImage.FileName}' is missing.");

            await using Stream imageStream = imageEntry.Open();
            using var memoryStream = new MemoryStream();
            await imageStream.CopyToAsync(memoryStream, cancellationToken);

            string originalFileName = Path.GetFileName(manifestImage.OriginalFileName);
            if (string.IsNullOrWhiteSpace(originalFileName))
            {
                originalFileName = Path.GetFileName(manifestImage.FileName);
            }

            string nameRegex = manifestImage.NameRegex?.Trim() ?? string.Empty;
            string contentRegex = manifestImage.ContentRegex?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nameRegex) &&
                string.IsNullOrWhiteSpace(contentRegex) &&
                !string.IsNullOrWhiteSpace(manifestImage.Regex))
            {
                if (DiagramImageDefinition.NormalizeMatchTarget(manifestImage.MatchTarget) == DiagramImageDefinition.ContentMatchTarget)
                {
                    contentRegex = manifestImage.Regex.Trim();
                }
                else
                {
                    nameRegex = manifestImage.Regex.Trim();
                }
            }

            images.Add(new DiagramImageDefinition
            {
                Id = manifestImage.Id,
                Name = string.IsNullOrWhiteSpace(manifestImage.Name)
                    ? Path.GetFileNameWithoutExtension(originalFileName)
                    : manifestImage.Name.Trim(),
                Regex = DiagramImageDefinition.GetLegacyRegex(nameRegex, contentRegex),
                MatchTarget = DiagramImageDefinition.GetLegacyMatchTarget(nameRegex, contentRegex),
                NameRegex = nameRegex,
                ContentRegex = contentRegex,
                ResourceTypeFilter = DiagramImageDefinition.NormalizeResourceTypeFilter(manifestImage.ResourceTypeFilter),
                SortOrder = manifestImage.SortOrder,
                OriginalFileName = originalFileName,
                ImageDataBase64 = Convert.ToBase64String(memoryStream.ToArray())
            });
        }

        List<DiagramImageDefinition> orderedImages = images
            .Select((image, index) => new { Image = image, Index = index })
            .OrderBy(item => item.Image.SortOrder > 0 ? item.Image.SortOrder : item.Index + 1)
            .ThenBy(item => item.Index)
            .Select(item => item.Image)
            .ToList();

        return new DiagramImagePackageImportResult(orderedImages);
    }

    private static string CreateImageEntryName(
        string name,
        int index,
        string extension,
        HashSet<string> usedEntryNames)
    {
        string safeName = SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "image";
        }

        string entryName = $"{ImagesRoot}{index + 1:D3}-{safeName}{extension}";
        int suffix = 2;
        while (!usedEntryNames.Add(entryName))
        {
            entryName = $"{ImagesRoot}{index + 1:D3}-{safeName}-{suffix}{extension}";
            suffix++;
        }

        return entryName;
    }

    private static string SanitizeFileName(string? value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string text = value?.Trim() ?? string.Empty;
        var chars = text
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        return new string(chars).Trim(' ', '.', '-');
    }

    private static bool IsSafeImageEntryName(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            !entryName.StartsWith(ImagesRoot, StringComparison.OrdinalIgnoreCase) ||
            entryName.Contains('\\') ||
            entryName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return !entryName.EndsWith("/", StringComparison.Ordinal);
    }

    private static string GetImageExtension(string? originalFileName, byte[] bytes)
    {
        string extension = Path.GetExtension(originalFileName) ?? string.Empty;
        if (IsSupportedImageExtension(extension))
        {
            return extension.ToLowerInvariant();
        }

        return GuessImageExtension(bytes);
    }

    private static bool IsSupportedImageExtension(string? extension)
    {
        return extension?.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    private static string GuessImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47)
        {
            return ".png";
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46)
        {
            return ".gif";
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0x42 &&
            bytes[1] == 0x4D)
        {
            return ".bmp";
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
             (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A)))
        {
            return ".tif";
        }

        return ".img";
    }

    private static string GetContentType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }
}

public sealed class DiagramImagePackageManifest
{
    public int FormatVersion { get; set; }

    public DateTimeOffset ExportedAtUtc { get; set; }

    public string AppName { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public List<DiagramImagePackageManifestImage> Images { get; set; } = [];
}

public sealed class DiagramImagePackageManifestImage
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Regex { get; set; } = string.Empty;

    public string MatchTarget { get; set; } = DiagramImageDefinition.NameMatchTarget;

    public string NameRegex { get; set; } = string.Empty;

    public string ContentRegex { get; set; } = string.Empty;

    public string ResourceTypeFilter { get; set; } = DiagramImageDefinition.AnyResourceTypeFilter;

    public int SortOrder { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
}

public sealed record DiagramImagePackageExportResult(int ImageCount);

public sealed record DiagramImagePackageImportResult(List<DiagramImageDefinition> Images);

internal sealed class DiagramImagePackageImage
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Regex { get; set; } = string.Empty;

    public string MatchTarget { get; set; } = DiagramImageDefinition.NameMatchTarget;

    public string NameRegex { get; set; } = string.Empty;

    public string ContentRegex { get; set; } = string.Empty;

    public string ResourceTypeFilter { get; set; } = DiagramImageDefinition.AnyResourceTypeFilter;

    public int SortOrder { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public byte[] ImageBytes { get; set; } = [];
}
