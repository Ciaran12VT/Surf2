using System.IO;
using System.Text.RegularExpressions;
using Surf2.Models;

namespace Surf2.Services;

public sealed class JavaScriptReferenceDefinitionParser : IReferenceDefinitionParser
{
    private static readonly Regex ClassPattern = new(
        @"^\s*(?:export\s+default\s+|export\s+)?class\s+([A-Za-z_$][A-Za-z0-9_$]*)\b",
        RegexOptions.Compiled);

    private static readonly Regex FunctionPattern = new(
        @"^\s*(?:export\s+default\s+|export\s+)?(?:async\s+)?function\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex AssignedFunctionPattern = new(
        @"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:async\s*)?(?:function\b|class\b|\([^)]*\)\s*=>|[A-Za-z_$][A-Za-z0-9_$]*\s*=>)",
        RegexOptions.Compiled);

    public bool CanParse(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".js", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ReferenceEntity> Parse(string filePath, string content)
    {
        string[] lines = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            Match classMatch = ClassPattern.Match(line);
            if (classMatch.Success)
            {
                yield return CreateEntity(classMatch.Groups[1].Value, ReferenceEntityKind.Class, filePath, i + 1, classMatch.Groups[1].Index + 1);
                continue;
            }

            Match functionMatch = FunctionPattern.Match(line);
            if (functionMatch.Success)
            {
                yield return CreateEntity(functionMatch.Groups[1].Value, ReferenceEntityKind.Function, filePath, i + 1, functionMatch.Groups[1].Index + 1);
                continue;
            }

            Match assignedFunctionMatch = AssignedFunctionPattern.Match(line);
            if (assignedFunctionMatch.Success)
            {
                ReferenceEntityKind kind = line.Contains("= class", StringComparison.Ordinal) ? ReferenceEntityKind.Class : ReferenceEntityKind.Function;
                yield return CreateEntity(assignedFunctionMatch.Groups[1].Value, kind, filePath, i + 1, assignedFunctionMatch.Groups[1].Index + 1);
            }
        }
    }

    private static ReferenceEntity CreateEntity(string name, ReferenceEntityKind kind, string filePath, int lineNumber, int columnNumber)
    {
        return new ReferenceEntity
        {
            Name = name,
            QualifiedName = name,
            Kind = kind,
            FilePath = filePath,
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            EndLineNumber = lineNumber,
            EndColumnNumber = columnNumber,
            Language = "JavaScript"
        };
    }
}
