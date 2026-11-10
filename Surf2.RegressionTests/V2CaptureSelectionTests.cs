using Surf2.Services.RelationalSnapshots;

namespace Surf2.RegressionTests;

internal static class V2CaptureSelectionTests
{
    public static void Run()
    {
        var table = new RelationalSourceTable(1, "dbo", "order] items");
        Equal("SELECT * FROM [dbo].[order]] items]", table.CreateReadQuery());
        Equal("SELECT TOP (100) * FROM [dbo].[order]] items]", (table with { RowSelection = "TOP 100" }).CreateReadQuery());
        Equal("SELECT * FROM [dbo].[order]] items] WHERE [Name] = N'where; top'",
            (table with { RowSelection = "WHERE [Name] = N'where; top'" }).CreateReadQuery());
        foreach (var invalid in new[] { "TOP 0", "TOP nope", "WHERE", "WHERE 1=1; DELETE FROM dbo.X" })
        {
            try { (table with { RowSelection = invalid }).CreateReadQuery(); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid row selection was accepted: " + invalid);
        }
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
