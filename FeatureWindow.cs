using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
namespace AtmosProfile;

internal static class FeatureWindow
{
    public static Window Owner { get; private set; } = null!;
    public static int ActiveOperations;
    static int width, minimumHeight;
    static readonly Subclass Callback = WindowProc;
    delegate IntPtr Subclass(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMax { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowSubclass(IntPtr hwnd, Subclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    public static void Attach(Window window)
    {
        Owner = window;
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) presenter.IsMaximizable = false;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        width = window.AppWindow.Size.Width; minimumHeight = (int)(640 * GetDpiForWindow(handle) / 96.0);
        window.AppWindow.Resize(new(width, Math.Max(window.AppWindow.Size.Height, (int)(720 * GetDpiForWindow(handle) / 96.0))));
        if (!SetWindowSubclass(handle, Callback, 1, 0)) throw new InvalidOperationException("Window sizing could not be configured.");
        window.AppWindow.Closing += (_, e) => { if (ActiveOperations > 0) e.Cancel = true; };
    }
    static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == 0x0024) {
            var limits = Marshal.PtrToStructure<MinMax>(lParam);
            limits.MinTrackSize.X = limits.MaxTrackSize.X = width;
            limits.MinTrackSize.Y = minimumHeight;
            Marshal.StructureToPtr(limits, lParam, false);
        }
        if (message == 0x02e0) {
            width = (int)(560 * GetDpiForWindow(hwnd) / 96.0);
            minimumHeight = (int)(640 * GetDpiForWindow(hwnd) / 96.0);
        }
        return result;
    }
}
