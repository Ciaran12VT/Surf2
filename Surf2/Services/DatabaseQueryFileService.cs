using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Windows;

namespace Surf2.Services;

public sealed class DatabaseQueryFileService
{
    private readonly Action<ProcessStartInfo> _openExplorer;

    public DatabaseQueryFileService(Action<ProcessStartInfo>? openExplorer = null)
    {
        _openExplorer = openExplorer ?? (startInfo => Process.Start(startInfo)?.Dispose());
    }

    public string CreateQueryFile(string query)
    {
        return TextClipboardService.CreateTextFile(
            query,
            $"database-metadata-query-{Guid.NewGuid():N}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public void CopyFileToClipboard(string filePath)
    {
        var fileDropList = new StringCollection
        {
            filePath
        };
        var dataObject = new DataObject();
        dataObject.SetFileDropList(fileDropList);
        Clipboard.SetDataObject(dataObject, copy: true);
    }

    public void OpenInExplorer(string filePath)
    {
        _openExplorer(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{filePath}\"",
            UseShellExecute = true
        });
    }
}
