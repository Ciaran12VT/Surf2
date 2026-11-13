using System.Data;
using Microsoft.Data.SqlClient;

namespace Surf2.Storage.Relational;

/// <summary>Additive preference fields; the published base schema and its checksum remain unchanged.</summary>
internal static class RelationalPreferenceSchema
{
    internal const string IsInstalledSql = """
SELECT CONVERT(bit,CASE WHEN COL_LENGTH('surf.ApplicationPreference','DefaultDarkBackcolor') IS NOT NULL
 AND COL_LENGTH('surf.ApplicationPreference','OpenDiagramDocumentationOnDoubleClick') IS NOT NULL
 AND COL_LENGTH('surf.ApplicationPreference','ShowExplorerPreview') IS NOT NULL
 AND COL_LENGTH('surf.ExtensionAppearance','DarkBackcolor') IS NOT NULL THEN 1 ELSE 0 END);
""";

    internal static async Task InstallAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 120;
        command.CommandText = """
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock @Resource=N'Surf2.PreferenceThemeSchema',
 @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
IF @LockResult<0 THROW 51000,'Could not acquire the preference upgrade lock.',1;
IF COL_LENGTH('surf.ApplicationPreference','DefaultDarkBackcolor') IS NULL
BEGIN
 ALTER TABLE surf.ApplicationPreference ADD DefaultDarkBackcolor nvarchar(max) NOT NULL
  CONSTRAINT DF_ApplicationPreference_DefaultDarkBackcolor DEFAULT N'';
 EXEC(N'UPDATE surf.ApplicationPreference SET DefaultDarkBackcolor=DefaultBackcolor;');
END;
IF COL_LENGTH('surf.ApplicationPreference','OpenDiagramDocumentationOnDoubleClick') IS NULL
 ALTER TABLE surf.ApplicationPreference ADD OpenDiagramDocumentationOnDoubleClick bit NOT NULL
  CONSTRAINT DF_ApplicationPreference_OpenDiagramDocumentation DEFAULT 1;
IF COL_LENGTH('surf.ApplicationPreference','ShowExplorerPreview') IS NULL
 ALTER TABLE surf.ApplicationPreference ADD ShowExplorerPreview bit NOT NULL
  CONSTRAINT DF_ApplicationPreference_ShowExplorerPreview DEFAULT 1;
IF COL_LENGTH('surf.ExtensionAppearance','DarkBackcolor') IS NULL
BEGIN
 ALTER TABLE surf.ExtensionAppearance ADD DarkBackcolor nvarchar(max) NOT NULL
  CONSTRAINT DF_ExtensionAppearance_DarkBackcolor DEFAULT N'';
 EXEC(N'UPDATE surf.ExtensionAppearance SET DarkBackcolor=Backcolor;');
END;
""";
        using var cancellation = RelationalSession.CancelCommand(command, ct);
        await command.ExecuteNonQueryAsync(ct);
    }
}
