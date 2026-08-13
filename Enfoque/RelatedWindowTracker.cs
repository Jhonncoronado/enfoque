using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Enfoque;

/// <summary>
/// Detecta ventanas emergentes relacionadas con la ventana objetivo mediante
/// eventos de accesibilidad de Windows.
/// </summary>
public sealed class RelatedWindowTracker : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjidWindow = 0;
    private const uint GwOwner = 4;
    private const uint GaRootOwner = 3;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;

    private readonly string _processName;
    private readonly IntPtr _originalWindow;
    private readonly IntPtr _originalRootOwner;
    private readonly bool _captureAnyPopup;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly WinEventDelegate _callback;
    private readonly Dictionary<IntPtr, DateTime> _recentlyShown = [];
    private IntPtr _hook;

    public event Action<IntPtr>? WindowShown;

    public RelatedWindowTracker(string? processName, IntPtr originalWindow,
        bool captureAnyPopup)
    {
        _processName = processName ?? string.Empty;
        _originalWindow = originalWindow;
        _originalRootOwner = GetAncestor(originalWindow, GaRootOwner);
        _captureAnyPopup = captureAnyPopup;
        _callback = OnWinEvent;

        if (_processName.Length > 0 || _captureAnyPopup)
        {
            _hook = SetWinEventHook(EventObjectShow, EventSystemForeground, IntPtr.Zero,
                _callback, 0, 0, WineventOutOfContext);
        }
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint threadId, uint eventTime)
    {
        if (hwnd == IntPtr.Zero || hwnd == _originalWindow || !IsWindowVisible(hwnd)) return;

        if (eventType == EventSystemForeground)
        {
            if (_captureAnyPopup && _recentlyShown.TryGetValue(hwnd, out var shownAt) &&
                DateTime.UtcNow - shownAt < TimeSpan.FromSeconds(10))
            {
                _recentlyShown.Remove(hwnd);
                WindowShown?.Invoke(hwnd);
            }
            return;
        }

        if (eventType != EventObjectShow || idObject != ObjidWindow || idChild != 0)
            return;

        var owner = GetWindow(hwnd, GwOwner);
        if (owner == hwnd) return;

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & WsChild) != 0) return;

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == _ownProcessId) return;

        try
        {
            var sameProcess = string.Equals(
                Process.GetProcessById((int)processId).ProcessName,
                _processName, StringComparison.OrdinalIgnoreCase);
            var sameOwnerTree = _originalRootOwner != IntPtr.Zero &&
                GetAncestor(hwnd, GaRootOwner) == _originalRootOwner;

            // Algunos navegadores crean el popup en otro proceso, pero
            // conservan la ventana propietaria de la página original.
            if (sameProcess || sameOwnerTree)
                WindowShown?.Invoke(hwnd);
            else if (_captureAnyPopup)
                _recentlyShown[hwnd] = DateTime.UtcNow;
        }
        catch { /* La ventana pudo cerrarse durante la detección. */ }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
        IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint msEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
