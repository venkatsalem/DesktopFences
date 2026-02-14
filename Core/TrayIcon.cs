using DesktopFences.Interop;
using DesktopFences.Models;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Core;

/// <summary>
/// Manages the system tray (notification area) icon.
/// Provides exit and edit-mode toggle. Zero network calls.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly AppConfig _config;
    private readonly Action _requestRedraw;
    private readonly Action _saveConfig;
    private readonly Action _exitApp;
    private NOTIFYICONDATAW _nid;
    private IntPtr _trayIconHandle;
    private bool _created;

    // Tray menu command IDs
    private const nuint CMD_EDIT_MODE = 100;
    private const nuint CMD_START_WITH_WIN = 101;
    private const nuint CMD_EXIT = 102;

    public TrayIcon(IntPtr hwnd, AppConfig config, Action requestRedraw, Action saveConfig, Action exitApp)
    {
        _hwnd = hwnd;
        _config = config;
        _requestRedraw = requestRedraw;
        _saveConfig = saveConfig;
        _exitApp = exitApp;
    }

    public unsafe void Create()
    {
        // Use shell32.dll icon index 35 (desktop icon) as tray icon
        _trayIconHandle = ExtractIconW(GetModuleHandleW(IntPtr.Zero), "shell32.dll", 35);
        if (_trayIconHandle == IntPtr.Zero)
        {
            // Fallback to default application icon
            _trayIconHandle = ExtractIconW(GetModuleHandleW(IntPtr.Zero), "shell32.dll", 15);
        }

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _trayIconHandle,
            uVersion = NOTIFYICON_VERSION_4
        };

        // Copy tip text into fixed buffer
        fixed (char* tip = _nid.szTip)
        {
            CopyStringToFixedBuffer(tip, 128, "Desktop Fences");
        }

        Shell_NotifyIconW(NIM_ADD, ref _nid);
        Shell_NotifyIconW(NIM_SETVERSION, ref _nid);
        _created = true;
    }

    public void HandleMessage(IntPtr wParam, IntPtr lParam)
    {
        uint msg = (uint)(lParam.ToInt64() & 0xFFFF);

        switch (msg)
        {
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowTrayMenu();
                break;

            case NIN_SELECT:
            case NIN_KEYSELECT:
            case WM_LBUTTONDBLCLK:
                // Toggle edit mode on double-click
                _config.EditMode = !_config.EditMode;
                _saveConfig();
                _requestRedraw();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        GetCursorPos(out var pt);

        var menu = CreatePopupMenu();

        uint editFlags = MF_STRING | (_config.EditMode ? MF_CHECKED : MF_UNCHECKED);
        uint startFlags = MF_STRING | (_config.StartWithWindows ? MF_CHECKED : MF_UNCHECKED);

        InsertMenuW(menu, 0, editFlags, CMD_EDIT_MODE, "Edit Mode");
        InsertMenuW(menu, 1, startFlags, CMD_START_WITH_WIN, "Start with Windows");
        InsertMenuW(menu, 2, MF_SEPARATOR, 0, null);
        InsertMenuW(menu, 3, MF_STRING, CMD_EXIT, "Exit");

        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_BOTTOMALIGN,
            pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);

        switch ((nuint)cmd)
        {
            case CMD_EDIT_MODE:
                _config.EditMode = !_config.EditMode;
                _saveConfig();
                _requestRedraw();
                break;

            case CMD_START_WITH_WIN:
                _config.StartWithWindows = !_config.StartWithWindows;
                RegistryStartup.SetStartWithWindows(_config.StartWithWindows);
                _saveConfig();
                break;

            case CMD_EXIT:
                _exitApp();
                break;
        }
    }

    public void Dispose()
    {
        if (_created)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _created = false;
        }
        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }
    }

    /// <summary>Helper to copy a managed string into a fixed char buffer.</summary>
    private static unsafe void CopyStringToFixedBuffer(char* buffer, int bufferSize, string text)
    {
        int len = Math.Min(text.Length, bufferSize - 1);
        for (int i = 0; i < len; i++)
            buffer[i] = text[i];
        buffer[len] = '\0';
    }
}
