using Surf2.Storage.Relational.State;

namespace Surf2;

internal sealed class RelationalWorkbenchPickerWindow : RelationalQueryPickerWindow<WorkbenchSummary>
{
    public RelationalWorkbenchPickerWindow(RelationalStateStore store) : base("Workbenches", async (cursor, ct) =>
    {
        var page = await store.ListRecentWorkbenchesAsync(100, cursor as RuntimeWorkbenchCursor, ct);
        return new(page.Items, page.Next);
    }, Label) { }

    internal static string Label(WorkbenchSummary s)
    {
        var updated = s.UpdatedAtUtc != default ? s.UpdatedAtUtc : s.SavedAtUtc != default ? s.SavedAtUtc : s.CreatedAtUtc;
        string name = !string.IsNullOrWhiteSpace(s.Name) ? s.Name : s.ScopeName;
        return name + " - " + updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
