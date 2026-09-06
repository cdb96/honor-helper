using System;
using System.Runtime.InteropServices;

namespace HonorHelper;

/// <summary>
/// A hidden Win32 message-only window that the UI creates to receive cross-process
/// commands from the background service (show / hide / quit the main window). The
/// service broadcasts <see cref="UiControlMessages"/> to HWND_BROADCAST; this window
/// pipes them into managed event handlers.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private IntPtr _hwnd;
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private readonly IntPtr _module;
    private bool _disposed;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Raised when the service requests the main window be shown/activated.</summary>
    public event Action? ShowRequested;
    /// <summary>Raised when the service requests the main window be hidden.</summary>
    public event Action? HideRequested;
    /// <summary>Raised when the service requests the UI process to quit.</summary>
    public event Action? QuitRequested;

    public MessageWindow()
    {
        _className = "HonorHelperMsg_" + Guid.NewGuid().ToString("N");
        _module = GetModuleHandle(null);
        _wndProc = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = _className,
            hInstance = _module,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        GC.KeepAlive(_wndProc);
    }

    public IntPtr Handle => _hwnd;

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == UiControlMessages.Show) { ShowRequested?.Invoke(); return IntPtr.Zero; }
        if (msg == UiControlMessages.Hide) { HideRequested?.Invoke(); return IntPtr.Zero; }
        if (msg == UiControlMessages.Quit) { QuitRequested?.Invoke(); return IntPtr.Zero; }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ShowRequested = null;
        HideRequested = null;
        QuitRequested = null;
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        UnregisterClass(_className, _module);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
