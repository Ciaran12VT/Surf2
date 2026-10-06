using System.Text.Json.Serialization;
using Surf2.Models;
using Surf2.Storage.Relational.State;

namespace Surf2.Storage.Relational.Access.State;

public enum StateLoadStatus { Ready, Missing, Failed, Cancelled }

/// <summary>No fallback model or write capability is returned on a missing/failed/cancelled read.</summary>
public sealed class StateLoad<T> where T : class
{
    private StateLoad(StateLoadStatus status, T? value, Exception? error) { Status = status; Value = value; Error = error; }
    public StateLoadStatus Status { get; }
    [JsonIgnore] public T? Value { get; }
    [JsonIgnore] public Exception? Error { get; }
    public bool IsReady => Status == StateLoadStatus.Ready;
    internal static StateLoad<T> Ready(T value) => new(StateLoadStatus.Ready, value, null);
    internal static StateLoad<T> Missing() => new(StateLoadStatus.Missing, null, null);
    internal static StateLoad<T> Failed(Exception error) => new(StateLoadStatus.Failed, null, error);
    internal static StateLoad<T> Cancelled() => new(StateLoadStatus.Cancelled, null, null);
    public override string ToString() => nameof(StateLoad<T>) + ": " + Status;
}

// The generic type binds a cursor to one catalogue, not just coincidentally equal owner keys/generations.
public sealed class StateCatalogueCursor<T>
{
    private readonly StateCursor _cursor;
    internal StateCatalogueCursor(StateCursor cursor) => _cursor = cursor with { Generation = cursor.Generation.ToArray() };
    public Guid Epoch => _cursor.Epoch;
    internal StateCursor Native => _cursor with { Generation = _cursor.Generation.ToArray() };
}

public sealed record StateCataloguePage<T>(IReadOnlyList<T> Items, StateCatalogueCursor<T>? Next);

public sealed class PreferenceCursor<T>
{
    internal PreferenceCursor(StateToken owner, long ordinal, long key) { Owner = owner; Ordinal = ordinal; Key = key; }
    public StateToken Owner { get; }
    internal long Ordinal { get; }
    internal long Key { get; }
}

public sealed record PreferencePage<T>(StateToken Owner, IReadOnlyList<T> Items, PreferenceCursor<T>? Next);
public sealed record ExtensionAppearanceSummary(long Key, long SortOrdinal, string Extension, string Backcolor, string Language);
public sealed record ReferenceStyleSummary(long Key, long SortOrdinal, string Language, ReferenceEntityKind Kind,
    string Foreground, bool IsBold, bool IsItalic, bool IsUnderline);
public sealed record ImageDefinitionSummary(long Key, long SortOrdinal, string Id, string Name, int SortOrder,
    string ResourceTypeFilter, bool HasAsset);

public sealed record StateInputPreferences(int Version, bool EnableCanvasCtrlMousePanning, bool EnableTabCtrlMouseScrolling,
    bool EnableTabCtrlShiftMouseAutoscrolling, bool EnableCodeShiftMouseAutoscrolling,
    bool EnableCodeCtrlShiftMouseScrollbarLockedScrolling, bool EnableCodeCanvasShiftMousePanning,
    bool EnableCodeCanvasCtrlShiftMouseZooming, bool EnableCodeViewCtrlPlusMinusNavigation,
    bool EnableCtrlNumberViewSwitching, bool EnableCodeTabCtrlASNavigation, bool EnableDiagramCtrlQSidebarToggle,
    bool EnableDiagramCtrlWWorkflowSidebar, bool EnableDiagramShiftMousePanning, bool EnableDiagramCtrlShiftMouseZooming)
{
    internal static StateInputPreferences From(KeyboardShortcutSettings v) => new(v.Version, v.EnableCanvasCtrlMousePanning,
        v.EnableTabCtrlMouseScrolling, v.EnableTabCtrlShiftMouseAutoscrolling, v.EnableCodeShiftMouseAutoscrolling,
        v.EnableCodeCtrlShiftMouseScrollbarLockedScrolling, v.EnableCodeCanvasShiftMousePanning, v.EnableCodeCanvasCtrlShiftMouseZooming,
        v.EnableCodeViewCtrlPlusMinusNavigation, v.EnableCtrlNumberViewSwitching, v.EnableCodeTabCtrlASNavigation,
        v.EnableDiagramCtrlQSidebarToggle, v.EnableDiagramCtrlWWorkflowSidebar, v.EnableDiagramShiftMousePanning, v.EnableDiagramCtrlShiftMouseZooming);

    public KeyboardShortcutSettings ToRuntimeSettings() => new()
    {
        Version = Version, EnableCanvasCtrlMousePanning = EnableCanvasCtrlMousePanning, EnableTabCtrlMouseScrolling = EnableTabCtrlMouseScrolling,
        EnableTabCtrlShiftMouseAutoscrolling = EnableTabCtrlShiftMouseAutoscrolling, EnableCodeShiftMouseAutoscrolling = EnableCodeShiftMouseAutoscrolling,
        EnableCodeCtrlShiftMouseScrollbarLockedScrolling = EnableCodeCtrlShiftMouseScrollbarLockedScrolling,
        EnableCodeCanvasShiftMousePanning = EnableCodeCanvasShiftMousePanning, EnableCodeCanvasCtrlShiftMouseZooming = EnableCodeCanvasCtrlShiftMouseZooming,
        EnableCodeViewCtrlPlusMinusNavigation = EnableCodeViewCtrlPlusMinusNavigation, EnableCtrlNumberViewSwitching = EnableCtrlNumberViewSwitching,
        EnableCodeTabCtrlASNavigation = EnableCodeTabCtrlASNavigation, EnableDiagramCtrlQSidebarToggle = EnableDiagramCtrlQSidebarToggle,
        EnableDiagramCtrlWWorkflowSidebar = EnableDiagramCtrlWWorkflowSidebar, EnableDiagramShiftMousePanning = EnableDiagramShiftMousePanning,
        EnableDiagramCtrlShiftMouseZooming = EnableDiagramCtrlShiftMouseZooming
    };
}

/// <summary>Scalar preferences only; neither an AppSettings master model nor image/style collections.</summary>
public sealed record StartupPreferences(string Theme, bool LoadMostRecentWorkbenchOnStartup, bool IgnoreWhitespaceByDefault,
    bool IgnoreCaseByDefault, bool EnableInternalLogging, string DefaultBackcolor, StateInputPreferences Input)
{
    internal static StartupPreferences From(PreferenceSummary v) => new(v.Theme, v.LoadMostRecentWorkbenchOnStartup,
        v.IgnoreWhitespaceByDefault, v.IgnoreCaseByDefault, v.EnableInternalLogging, v.DefaultBackcolor, StateInputPreferences.From(v.KeyboardShortcuts));

    // Applies only the loaded scalar subset. Existing child collections remain untouched.
    public void ApplyToRuntime(AppSettings destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Appearance ??= new(); destination.CodeWindows ??= new(); destination.ResourceComparison ??= new(); destination.Diagnostics ??= new();
        destination.Appearance.Theme = Theme;
        destination.CodeWindows.DefaultBackcolor = DefaultBackcolor;
        destination.LoadMostRecentWorkbenchOnStartup = LoadMostRecentWorkbenchOnStartup;
        destination.ResourceComparison.IgnoreWhitespaceByDefault = IgnoreWhitespaceByDefault;
        destination.ResourceComparison.IgnoreCaseByDefault = IgnoreCaseByDefault;
        destination.Diagnostics.EnableInternalLogging = EnableInternalLogging;
        destination.KeyboardShortcuts = Input.ToRuntimeSettings();
    }

    internal AppSettings HeadModel() { var value = new AppSettings(); ApplyToRuntime(value); return value; }
}

public sealed class SelectedImageAsset
{
    private readonly byte[] _bytes;
    internal SelectedImageAsset(long key, StateToken owner, string mediaType, byte[] bytes)
    { DefinitionKey = key; Owner = owner; MediaType = mediaType; _bytes = bytes; }
    public long DefinitionKey { get; }
    public StateToken Owner { get; }
    public string MediaType { get; }
    public long ByteCount => _bytes.LongLength;
    public byte[] CopyBytes() => _bytes.ToArray();
    public override string ToString() => nameof(SelectedImageAsset) + ": " + ByteCount + " bytes";
}

public enum StateEditStatus { Clean, Dirty, Saving, Conflict, OutcomeUnknown, Closed }
public enum StateSaveDisposition { NoChanges, Saved }
public sealed record StateSaveResult(StateSaveDisposition Disposition, StateToken Token, long SavedGeneration, bool HasNewerChanges);
public enum StateRecoveryDisposition { NoPendingSave, Committed, NotCommitted, Conflict }
