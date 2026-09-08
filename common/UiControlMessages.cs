using System;
using System.Runtime.InteropServices;

namespace HonorHelper;

/// <summary>
/// Cross-process UI control messages sent by the background service to the UI process
/// to show/hide/quit the main window. Both the service (sender) and the UI (listener)
/// register the same window-message names, so a broadcast from the service reaches the
/// UI's hidden message window even though the two are separate processes.
/// </summary>
public static class UiControlMessages
{
    public const string ShowUiName = "HonorHelperShowUi";
    public const string HideUiName = "HonorHelperHideUi";
    public const string QuitUiName = "HonorHelperQuitUi";
    public const string StateChangedName = "HonorHelperStateChanged";

    public static readonly uint Show = RegisterWindowMessage(ShowUiName);
    public static readonly uint Hide = RegisterWindowMessage(HideUiName);
    public static readonly uint Quit = RegisterWindowMessage(QuitUiName);
    public static readonly uint StateChanged = RegisterWindowMessage(StateChangedName);

    /// <summary>Broadcast a message to all top-level windows on this desktop.</summary>
    public static void Broadcast(uint message)
        => PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, message, IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
