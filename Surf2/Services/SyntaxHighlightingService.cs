using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Surf2.Models;

namespace Surf2.Services;

public sealed class SyntaxHighlightingService
{
    private const string SqlDefinitionName = "SurfSql";

    public SyntaxHighlightingService()
    {
        RegisterSqlHighlighting();
    }

    public IHighlightingDefinition? GetDefinition(string filePath, string? language = null)
    {
        string normalizedLanguage = CodeWindowSettings.NormalizeLanguage(language);
        if (!string.IsNullOrWhiteSpace(language))
        {
            return normalizedLanguage switch
            {
                CodeWindowSettings.CSharpLanguage => HighlightingManager.Instance.GetDefinitionByExtension(".cs"),
                CodeWindowSettings.VisualBasicLanguage => HighlightingManager.Instance.GetDefinitionByExtension(".vb"),
                CodeWindowSettings.SqlServerLanguage => HighlightingManager.Instance.GetDefinition(SqlDefinitionName),
                CodeWindowSettings.JavaScriptLanguage => HighlightingManager.Instance.GetDefinitionByExtension(".js"),
                CodeWindowSettings.XmlLanguage => HighlightingManager.Instance.GetDefinitionByExtension(".xml"),
                _ => null
            };
        }

        string extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension == ".sql")
        {
            return HighlightingManager.Instance.GetDefinition(SqlDefinitionName);
        }

        if (extension == ".vbs")
        {
            return HighlightingManager.Instance.GetDefinitionByExtension(".vb");
        }

        return HighlightingManager.Instance.GetDefinitionByExtension(extension);
    }

    private static void RegisterSqlHighlighting()
    {
        if (HighlightingManager.Instance.GetDefinition(SqlDefinitionName) != null)
        {
            return;
        }

        try
        {
            using var stringReader = new StringReader(SqlXshd);
            using var xmlReader = XmlReader.Create(stringReader);
            IHighlightingDefinition definition = HighlightingLoader.Load(xmlReader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(SqlDefinitionName, [".sql"], definition);
        }
        catch (FormatException)
        {
            // SQL files can still open as plain text if the highlighting definition is invalid.
        }
        catch (XmlException)
        {
            // SQL files can still open as plain text if the highlighting definition is invalid.
        }
    }

    private const string SqlXshd = """
<SyntaxDefinition name="SurfSql" extensions=".sql" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="#008000" />
  <Color name="String" foreground="#A31515" />
  <Color name="Number" foreground="#098658" />
  <Color name="Keyword" foreground="#0000FF" fontWeight="bold" />
  <Color name="Function" foreground="#795E26" />
  <RuleSet ignoreCase="true">
    <Span color="Comment" begin="--" end="\n" />
    <Span color="Comment" multiline="true" begin="/\*" end="\*/" />
    <Span color="String" begin="'" end="'" />
    <Span color="String" begin="&quot;" end="&quot;" />
    <Rule color="Number">\b\d+(\.\d+)?\b</Rule>
    <Keywords color="Keyword">
      <Word>ADD</Word>
      <Word>ALTER</Word>
      <Word>AND</Word>
      <Word>AS</Word>
      <Word>ASC</Word>
      <Word>BEGIN</Word>
      <Word>BETWEEN</Word>
      <Word>BY</Word>
      <Word>CASE</Word>
      <Word>CAST</Word>
      <Word>CREATE</Word>
      <Word>CROSS</Word>
      <Word>DECLARE</Word>
      <Word>DELETE</Word>
      <Word>DESC</Word>
      <Word>DISTINCT</Word>
      <Word>DROP</Word>
      <Word>ELSE</Word>
      <Word>END</Word>
      <Word>EXEC</Word>
      <Word>EXECUTE</Word>
      <Word>EXISTS</Word>
      <Word>FROM</Word>
      <Word>FULL</Word>
      <Word>FUNCTION</Word>
      <Word>GROUP</Word>
      <Word>HAVING</Word>
      <Word>IF</Word>
      <Word>IN</Word>
      <Word>INNER</Word>
      <Word>INSERT</Word>
      <Word>INTO</Word>
      <Word>IS</Word>
      <Word>JOIN</Word>
      <Word>LEFT</Word>
      <Word>LIKE</Word>
      <Word>MERGE</Word>
      <Word>NOT</Word>
      <Word>NULL</Word>
      <Word>ON</Word>
      <Word>OR</Word>
      <Word>ORDER</Word>
      <Word>OUTER</Word>
      <Word>PROCEDURE</Word>
      <Word>RIGHT</Word>
      <Word>SELECT</Word>
      <Word>SET</Word>
      <Word>TABLE</Word>
      <Word>THEN</Word>
      <Word>TOP</Word>
      <Word>UNION</Word>
      <Word>UPDATE</Word>
      <Word>VALUES</Word>
      <Word>VIEW</Word>
      <Word>WHEN</Word>
      <Word>WHERE</Word>
      <Word>WITH</Word>
    </Keywords>
    <Keywords color="Function">
      <Word>COUNT</Word>
      <Word>DATEADD</Word>
      <Word>DATEDIFF</Word>
      <Word>GETDATE</Word>
      <Word>ISNULL</Word>
      <Word>MAX</Word>
      <Word>MIN</Word>
      <Word>SUM</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>
""";
}
