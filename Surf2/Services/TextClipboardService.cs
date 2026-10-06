using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;

namespace Surf2.Services;

public static class TextClipboardService
{
    public static void CopyText(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            Clipboard.Clear();
            return;
        }

        Clipboard.SetText(content);
    }

    public static string CopyAsTxtFile(string? content, string? fileNameSeed)
    {
        string text = content ?? string.Empty;
        string filePath = CreateTextFile(text, fileNameSeed);

        var fileDropList = new StringCollection
        {
            filePath
        };

        var dataObject = new DataObject();
        dataObject.SetFileDropList(fileDropList);
        if (text.Length > 0)
        {
            dataObject.SetText(text);
        }

        Clipboard.SetDataObject(dataObject, copy: true);

        return filePath;
    }

    private static string CreateTextFile(string content, string? fileNameSeed)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "Surf2", "ClipboardText", Guid.NewGuid().ToString("N"));
        int maximumFileNameLength = Math.Min(255, 259 - directoryPath.Length - 1);
        string fileName = CreateSafeTextFileName(fileNameSeed, DateTime.Now, maximumFileNameLength);
        Directory.CreateDirectory(directoryPath);

        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    private static string CreateSafeTextFileName(string? fileNameSeed, DateTime timestamp, int maximumFileNameLength)
    {
        string suffix = $"-{timestamp.ToString("yyyyMMdd_HH_mm_ss", CultureInfo.InvariantCulture)}.txt";
        int maximumStemLength = maximumFileNameLength - suffix.Length;
        if (maximumStemLength < 1)
        {
            throw new IOException("The temporary folder path is too long for a clipboard text file.");
        }

        string candidate = string.IsNullOrWhiteSpace(fileNameSeed)
            ? "resource"
            : fileNameSeed.Trim();

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalidChar, '_');
        }

        candidate = candidate.Trim('.', ' ');
        if (candidate.Length == 0)
        {
            candidate = "resource";
        }

        string deviceName = candidate.Split('.')[0];
        if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceName.Length == 4 &&
             (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             deviceName[3] is >= '1' and <= '9'))
        {
            candidate = $"_{candidate}";
        }

        if (candidate.Length > maximumStemLength)
        {
            candidate = candidate[..maximumStemLength].Trim('.', ' ');
            if (candidate.Length > 0 && char.IsHighSurrogate(candidate[^1]))
            {
                candidate = candidate[..^1];
            }
        }

        return $"{(candidate.Length == 0 ? "_" : candidate)}{suffix}";
    }
}
