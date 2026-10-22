using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
namespace AtmosProfile;

internal static class FeatureWindow
{
    public static Window Owner { get; private set; } = null!;
    public static int ActiveOperations;
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
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        double scale = GetDpiForWindow(handle) / 96.0;
        window.AppWindow.Resize(new(Math.Min(window.AppWindow.Size.Width, area.Width), Math.Min((int)(720 * scale), area.Height)));
        if (!SetWindowSubclass(handle, Callback, 1, 0)) throw new InvalidOperationException("Window sizing could not be configured.");
        window.AppWindow.Closing += (_, e) => { if (ActiveOperations > 0) e.Cancel = true; };
    }
    static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == 0x0024) {
            var limits = Marshal.PtrToStructure<MinMax>(lParam);
            double scale = GetDpiForWindow(hwnd) / 96.0;
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(Owner.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
            limits.MinTrackSize.X = Math.Min((int)(480 * scale), area.Width);
            limits.MinTrackSize.Y = Math.Min((int)(420 * scale), area.Height);
            Marshal.StructureToPtr(limits, lParam, false);
        }
        return result;
    }
}
