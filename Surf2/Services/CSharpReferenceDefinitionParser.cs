using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Surf2.Models;

namespace Surf2.Services;

public sealed class CSharpReferenceDefinitionParser : IReferenceDefinitionParser
{
    public bool CanParse(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase);
    }

    public IEnumerable<ReferenceEntity> Parse(string filePath, string content)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(content).GetRoot();

        foreach (BaseTypeDeclarationSyntax type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            yield return CreateEntity(
                type.Identifier.ValueText,
                GetKind(type),
                filePath,
                type,
                GetQualifiedName(type, type.Identifier.ValueText),
                "C#");
        }

        foreach (DelegateDeclarationSyntax type in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            yield return CreateEntity(
                type.Identifier.ValueText,
                ReferenceEntityKind.Delegate,
                filePath,
                type,
                GetQualifiedName(type, type.Identifier.ValueText),
                "C#");
        }

        foreach (MethodDeclarationSyntax method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            yield return CreateEntity(
                method.Identifier.ValueText,
                ReferenceEntityKind.Method,
                filePath,
                method,
                GetQualifiedName(method, method.Identifier.ValueText),
                "C#",
                method.ParameterList);
        }

        foreach (ConstructorDeclarationSyntax constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            yield return CreateEntity(
                constructor.Identifier.ValueText,
                ReferenceEntityKind.Method,
                filePath,
                constructor,
                GetQualifiedName(constructor, constructor.Identifier.ValueText),
                "C#",
                constructor.ParameterList);
        }

        foreach (LocalFunctionStatementSyntax function in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            yield return CreateEntity(
                function.Identifier.ValueText,
                ReferenceEntityKind.Method,
                filePath,
                function,
                GetQualifiedName(function, function.Identifier.ValueText),
                "C#",
                function.ParameterList);
        }
    }

    private static ReferenceEntity CreateEntity(
        string name,
        ReferenceEntityKind kind,
        string filePath,
        SyntaxNode node,
        string qualifiedName,
        string language,
        BaseParameterListSyntax? parameterList = null)
    {
        FileLinePositionSpan lineSpan = node.GetLocation().GetLineSpan();
        (int? parameterCount, int? minimumArgumentCount, int? maximumArgumentCount) = GetArgumentRange(parameterList);

        return new ReferenceEntity
        {
            Name = name,
            QualifiedName = qualifiedName,
            Kind = kind,
            FilePath = filePath,
            LineNumber = lineSpan.StartLinePosition.Line + 1,
            ColumnNumber = lineSpan.StartLinePosition.Character + 1,
            EndLineNumber = lineSpan.EndLinePosition.Line + 1,
            EndColumnNumber = lineSpan.EndLinePosition.Character + 1,
            ParameterCount = parameterCount,
            MinimumArgumentCount = minimumArgumentCount,
            MaximumArgumentCount = maximumArgumentCount,
            Language = language,
            ContainerName = GetContainerName(node)
        };
    }

    private static ReferenceEntityKind GetKind(BaseTypeDeclarationSyntax type)
    {
        return type switch
        {
            ClassDeclarationSyntax => ReferenceEntityKind.Class,
            InterfaceDeclarationSyntax => ReferenceEntityKind.Interface,
            StructDeclarationSyntax => ReferenceEntityKind.Struct,
            EnumDeclarationSyntax => ReferenceEntityKind.Enum,
            RecordDeclarationSyntax => ReferenceEntityKind.Class,
            _ => ReferenceEntityKind.Class
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
                case BaseTypeDeclarationSyntax type:
                    parts.Push(type.Identifier.ValueText);
                    break;
                case NamespaceDeclarationSyntax namespaceDeclaration:
                    parts.Push(namespaceDeclaration.Name.ToString());
                    break;
                case FileScopedNamespaceDeclarationSyntax namespaceDeclaration:
                    parts.Push(namespaceDeclaration.Name.ToString());
                    break;
            }
        }

        return string.Join(".", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string GetContainerName(SyntaxNode node)
    {
        for (SyntaxNode? parent = node.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is BaseTypeDeclarationSyntax type)
            {
                return GetQualifiedName(type, type.Identifier.ValueText);
            }
        }

        return string.Empty;
    }

    private static (int? ParameterCount, int? MinimumArgumentCount, int? MaximumArgumentCount) GetArgumentRange(BaseParameterListSyntax? parameterList)
    {
        if (parameterList == null)
        {
            return (null, null, null);
        }

        int parameterCount = 0;
        int minimumArgumentCount = 0;
        int maximumArgumentCount = 0;
        bool hasParamsParameter = false;

        foreach (ParameterSyntax parameter in parameterList.Parameters)
        {
            bool isExtensionThisParameter = parameterCount == 0 &&
                parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ThisKeyword));
            parameterCount++;

            if (isExtensionThisParameter)
            {
                continue;
            }

            bool isParams = parameter.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.ParamsKeyword));
            if (isParams)
            {
                hasParamsParameter = true;
                continue;
            }

            maximumArgumentCount++;

            if (parameter.Default == null)
            {
                minimumArgumentCount++;
            }
        }

        return (parameterCount, minimumArgumentCount, hasParamsParameter ? int.MaxValue : maximumArgumentCount);
    }
}
