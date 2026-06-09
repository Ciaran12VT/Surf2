using Surf2.Models;

namespace Surf2;

internal static class DatabaseHistoryDisplay
{
    public static string GetResourceTypeDisplay(DatabaseVersionedResourceKind kind)
    {
        return kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => "Stored Procedure",
            DatabaseVersionedResourceKind.View => "View",
            DatabaseVersionedResourceKind.Function => "Function",
            DatabaseVersionedResourceKind.Trigger => "Trigger",
            DatabaseVersionedResourceKind.TableMetadata => "Table",
            DatabaseVersionedResourceKind.TableData => "Table Data",
            _ => "Resource"
        };
    }

    public static int GetResourceTypeSortOrder(DatabaseVersionedResourceKind kind)
    {
        return kind switch
        {
            DatabaseVersionedResourceKind.StoredProcedure => 0,
            DatabaseVersionedResourceKind.Function => 1,
            DatabaseVersionedResourceKind.View => 2,
            DatabaseVersionedResourceKind.Trigger => 3,
            DatabaseVersionedResourceKind.TableMetadata => 4,
            DatabaseVersionedResourceKind.TableData => 5,
            _ => 99
        };
    }

    public static string GetFileNameDisplay(string displayName, string relativePath)
    {
        string name = !string.IsNullOrWhiteSpace(displayName)
            ? displayName
            : relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? relativePath;

        foreach (string extension in new[] { ".txt", ".sql", ".csv" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^extension.Length];
            }
        }

        return name;
    }

    public static bool ContainsText(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
