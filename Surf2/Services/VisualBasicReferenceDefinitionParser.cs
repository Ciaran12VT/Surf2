using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Surf2.Models;

namespace Surf2.Services;

public sealed class VisualBasicReferenceDefinitionParser : IReferenceDefinitionParser
{
    public bool CanParse(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".vbs", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ReferenceEntity> Parse(string filePath, string content)
    {
        SyntaxNode root = VisualBasicSyntaxTree.ParseText(content).GetRoot();

        foreach (ClassBlockSyntax block in root.DescendantNodes().OfType<ClassBlockSyntax>())
        {
            yield return CreateEntity(block.ClassStatement.Identifier.ValueText, ReferenceEntityKind.Class, filePath, block.ClassStatement, "VB");
        }

        foreach (InterfaceBlockSyntax block in root.DescendantNodes().OfType<InterfaceBlockSyntax>())
        {
            yield return CreateEntity(block.InterfaceStatement.Identifier.ValueText, ReferenceEntityKind.Interface, filePath, block.InterfaceStatement, "VB");
        }

        foreach (StructureBlockSyntax block in root.DescendantNodes().OfType<StructureBlockSyntax>())
        {
            yield return CreateEntity(block.StructureStatement.Identifier.ValueText, ReferenceEntityKind.Struct, filePath, block.StructureStatement, "VB");
        }

        foreach (EnumBlockSyntax block in root.DescendantNodes().OfType<EnumBlockSyntax>())
        {
            yield return CreateEntity(block.EnumStatement.Identifier.ValueText, ReferenceEntityKind.Enum, filePath, block.EnumStatement, "VB");
        }

        foreach (ModuleBlockSyntax block in root.DescendantNodes().OfType<ModuleBlockSyntax>())
        {
            yield return CreateEntity(block.ModuleStatement.Identifier.ValueText, ReferenceEntityKind.Class, filePath, block.ModuleStatement, "VB");
        }

        foreach (MethodBlockSyntax block in root.DescendantNodes().OfType<MethodBlockSyntax>())
        {
            yield return CreateEntity(block.SubOrFunctionStatement.Identifier.ValueText, ReferenceEntityKind.Method, filePath, block.SubOrFunctionStatement, "VB");
        }

        foreach (MethodStatementSyntax statement in root.DescendantNodes().OfType<MethodStatementSyntax>())
        {
            if (statement.Parent is MethodBlockSyntax)
            {
                continue;
            }

            yield return CreateEntity(statement.Identifier.ValueText, ReferenceEntityKind.Method, filePath, statement, "VB");
        }
    }

    private static ReferenceEntity CreateEntity(
        string name,
        ReferenceEntityKind kind,
        string filePath,
        SyntaxNode node,
        string language)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();

        return new ReferenceEntity
        {
            Name = name,
            QualifiedName = GetQualifiedName(node, name),
            Kind = kind,
            FilePath = filePath,
            LineNumber = lineSpan.StartLinePosition.Line + 1,
            ColumnNumber = lineSpan.StartLinePosition.Character + 1,
            EndLineNumber = lineSpan.EndLinePosition.Line + 1,
            EndColumnNumber = lineSpan.EndLinePosition.Character + 1,
            Language = language,
            ContainerName = GetContainerName(node)
        };
    }

    private static string GetQualifiedName(SyntaxNode node, string name)
    {
        var parts = new Stack<string>();
        parts.Push(name);

        for (SyntaxNode? parent = node.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case ClassBlockSyntax block:
                    parts.Push(block.ClassStatement.Identifier.ValueText);
                    break;
                case InterfaceBlockSyntax block:
                    parts.Push(block.InterfaceStatement.Identifier.ValueText);
                    break;
                case StructureBlockSyntax block:
                    parts.Push(block.StructureStatement.Identifier.ValueText);
                    break;
                case ModuleBlockSyntax block:
                    parts.Push(block.ModuleStatement.Identifier.ValueText);
                    break;
                case NamespaceBlockSyntax block:
                    parts.Push(block.NamespaceStatement.Name.ToString());
                    break;
            }
        }

        return string.Join(".", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string GetContainerName(SyntaxNode node)
    {
        for (SyntaxNode? parent = node.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent)
            {
                case ClassBlockSyntax block:
                    return GetQualifiedName(block, block.ClassStatement.Identifier.ValueText);
                case InterfaceBlockSyntax block:
                    return GetQualifiedName(block, block.InterfaceStatement.Identifier.ValueText);
                case StructureBlockSyntax block:
                    return GetQualifiedName(block, block.StructureStatement.Identifier.ValueText);
                case ModuleBlockSyntax block:
                    return GetQualifiedName(block, block.ModuleStatement.Identifier.ValueText);
            }
        }

        return string.Empty;
    }
}
