using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Surf2;

public partial class StartupSplashWindow : Window
{
    private readonly nint _mainWindowHandle;

    public StartupSplashWindow(Window mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        // Resolve Windows' startup placement without showing/loading the main window,
        // before creating the splash's own native window.
        _mainWindowHandle = new WindowInteropHelper(mainWindow).EnsureHandle();
        InitializeComponent();
        SourceInitialized += (_, _) => CenterOnMainWindowMonitor();
        Loaded += (_, _) =>
        {
            CenterOnMainWindowMonitor();
            _ = Dispatcher.BeginInvoke(new Action(CenterOnMainWindowMonitor), DispatcherPriority.Loaded);
        };
    }

    private void CenterOnMainWindowMonitor()
    {
        nint splashHandle = new WindowInteropHelper(this).Handle;
        if (splashHandle == 0) return; // A queued layout callback may run after Close.
        nint monitor = MonitorFromWindow(_mainWindowHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // Keep monitor bounds and window size in native screen coordinates. WPF
        // Left/Top use DIPs and cannot be mixed with these on scaled displays.
        // The first move can change DPI/window size; measure again before centering.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!GetWindowRect(splashHandle, out var bounds)) return;
            int x = info.WorkArea.Left + (info.WorkArea.Right - info.WorkArea.Left - (bounds.Right - bounds.Left)) / 2;
            int y = info.WorkArea.Top + (info.WorkArea.Bottom - info.WorkArea.Top - (bounds.Bottom - bounds.Top)) / 2;
            if (!SetWindowPos(splashHandle, 0, x, y, 0, 0, NoSize | NoZOrder | NoActivate)) return;
        }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint NoSize = 0x0001, NoZOrder = 0x0004, NoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea, WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
}
