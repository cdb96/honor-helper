using System;
using System.Runtime.InteropServices;

namespace HonorHelper;

/// <summary>
/// Minimal Win32 tray icon (Shell_NotifyIconW) with a hidden message window.
/// Left click toggles the main window, right click opens a context menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP_TRAY = 0x8000;      // WM_APP
    private const int TrayCallbackId = 1;

    // 单实例激活消息：固定名（不带 GUID），跨进程同名同 ID，第二实例广播它唤醒已有实例
    private const string ActivateMsgName = "HonorHelperActivate";

    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;

    [Flags]
    private enum Nif : uint
    {
        Message = 0x01,
        Icon = 0x02,
        Tip = 0x04,
    }

    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;

    private IntPtr _hwnd;
    private uint _wmTray;
    private readonly uint _activateMsg;
    private bool _added;
    private readonly Guid _guid = Guid.NewGuid();
    private readonly WndProcDelegate _wndProcDelegate;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? ActivateRequested;

    public TrayIcon(IntPtr iconHandle, string tooltip)
    {
        var cls = "HonorHelperTray_" + _guid.ToString("N");
        _wmTray = RegisterWindowMessage("HonorHelperTrayMsg_" + _guid.ToString("N"));
        _activateMsg = RegisterWindowMessage(ActivateMsgName);

        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = cls,
            hInstance = GetModuleHandle(null),
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(0, cls, "", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        _iconHandle = iconHandle;
        _tooltip = tooltip;

        var data = BuildNotifyData(Nif.Message | Nif.Icon | Nif.Tip);
        Shell_NotifyIcon(NIM_ADD, ref data);
        _added = true;
        GC.KeepAlive(_wndProcDelegate);
    }

    private readonly IntPtr _iconHandle;
    private readonly string _tooltip;

    private NOTIFYICONDATA BuildNotifyData(Nif flags)
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayCallbackId,
            uFlags = (uint)(flags),
            uCallbackMessage = (uint)_wmTray,
            hIcon = _iconHandle,
            szTip = _tooltip,
        };
    }

    public void UpdateTooltip(string text)
    {
        if (!_added)
            return;
        var d = BuildNotifyData(Nif.Tip);
        d.szTip = text;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>
    /// Shows a native context menu at the given screen point.
    /// Returns the chosen id, or null if dismissed.
    /// </summary>
    public int? ShowContextMenu(int screenX, int screenY, params (int Id, string Text)[] items)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return null;

        for (int i = 0; i < items.Length; i++)
            AppendMenu(menu, 0x00000000, (UIntPtr)items[i].Id, items[i].Text); // MF_STRING

        SetForegroundWindow(_hwnd);
        // TPM_RETURNCMD | TPM_RIGHTBUTTON -> returns the chosen id synchronously
        int chosen = TrackPopupMenuRet(menu, 0x0100 | 0x0002, screenX, screenY, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        // swallow the WM_COMMAND that TrackPopupMenu posts for the selection
        var msg = new MSG();
        while (PeekMessage(out msg, _hwnd, 0x0000, 0x0000, 0x0001)) { }

        return chosen == 0 ? null : chosen;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _wmTray && wParam.ToInt64() == TrayCallbackId)
        {
            int l = lParam.ToInt32();
            int m = l & 0xFFFF;
            if (m == WmLButtonUp) { LeftClick?.Invoke(); return IntPtr.Zero; }
            if (m == WmRButtonUp) { RightClick?.Invoke(); return IntPtr.Zero; }
        }
        if (msg == _activateMsg)
        {
            ActivateRequested?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var d = BuildNotifyData(Nif.Message);
            Shell_NotifyIcon(NIM_DELETE, ref d);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 由第二实例调用：广播激活消息，让已有实例把窗口从托盘唤出来。
    /// 注册消息会话内同名同 ID，HWND_BROADCAST 发给所有顶层窗口，
    /// 只有本应用的托盘消息窗口会响应，其余进程一律忽略。
    /// </summary>
    public static void NotifyExistingInstance()
    {
        PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, RegisterWindowMessage(ActivateMsgName),
            IntPtr.Zero, IntPtr.Zero);
    }

    // ---- interop ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr idNewItem, string newItem);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenu")]
    [return: MarshalAs(UnmanagedType.I4)]
    private static extern int TrackPopupMenuRet(IntPtr menu, uint flags, int x, int y,
        int reserved, IntPtr hwnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
}
