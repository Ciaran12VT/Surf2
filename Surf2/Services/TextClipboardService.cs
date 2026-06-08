using System.Collections.Specialized;
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
        string directoryPath = Path.Combine(Path.GetTempPath(), "Surf2", "ClipboardText");
        Directory.CreateDirectory(directoryPath);

        string fileName = CreateSafeTextFileName(fileNameSeed);
        string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}-{DateTime.Now:yyyyMMddHHmmssfff}.txt";
        string filePath = Path.Combine(directoryPath, uniqueFileName);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    private static string CreateSafeTextFileName(string? fileNameSeed)
    {
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

        if (candidate.Length > 90)
        {
            candidate = candidate[..90].Trim('.', ' ');
        }

        string withoutExtension = Path.GetFileNameWithoutExtension(candidate);
        if (string.IsNullOrWhiteSpace(withoutExtension))
        {
            withoutExtension = "resource";
        }

        return $"{withoutExtension}.txt";
    }
}
