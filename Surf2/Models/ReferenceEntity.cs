using System.IO;

namespace Surf2.Models;

public sealed class ReferenceEntity
{
    public string Name { get; set; } = string.Empty;

    public string QualifiedName { get; set; } = string.Empty;

    public ReferenceEntityKind Kind { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public int LineNumber { get; set; } = 1;

    public int ColumnNumber { get; set; } = 1;

    public int EndLineNumber { get; set; } = 1;

    public int EndColumnNumber { get; set; } = 1;

    public int? ParameterCount { get; set; }

    public int? MinimumArgumentCount { get; set; }

    public int? MaximumArgumentCount { get; set; }

    public string Language { get; set; } = string.Empty;

    public string ContainerName { get; set; } = string.Empty;

    public string DisplayLabel
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(QualifiedName) ? Name : QualifiedName;
            string parameterLabel = ParameterCount.HasValue ? $" ({ParameterCount.Value} params)" : string.Empty;
            string location = $"{Path.GetFileName(FilePath)}:{LineNumber}";
            return $"{Kind}: {name}{parameterLabel}  ({location})";
        }
    }
}
