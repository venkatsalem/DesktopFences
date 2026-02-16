using System.Runtime.InteropServices;
using DesktopFences.Interop;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Core;

/// <summary>
/// A multi-line text editor dialog for fence notes.
/// Pure Win32 — no WinForms, no WPF, no XAML. Zero network connections.
/// </summary>
internal static partial class NoteEditDialog
{
    private const string DialogClassName = "DFNoteEditDlg";
    private const string EditClassName = "EDIT";
    private const string ButtonClassName = "BUTTON";

    private static string? _result;
    private static IntPtr _editHwnd;
    private static IntPtr _dialogHwnd;
    private static bool _registered;
    private static WndProc? _dlgProc;

    // Edit control styles
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_WANTRETURN = 0x1000;
    private const uint BS_PUSHBUTTON = 0x0000;
    private const uint BS_DEFPUSHBUTTON = 0x0001;

    // Window messages
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_THICKFRAME = 0x00040000;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, string? lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr EnableWindow(IntPtr hWnd, int bEnable);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int IsDialogMessageW(IntPtr hDlg, ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial int IsWindow(IntPtr hWnd);

    public static string? Show(IntPtr owner, string title, string? currentText)
    {
        _result = null;
        var hInstance = GetModuleHandleW(IntPtr.Zero);

        if (!_registered)
        {
            _dlgProc = DlgWndProc;
            var classNamePtr = Marshal.StringToHGlobalUni(DialogClassName);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_dlgProc),
                hInstance = hInstance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = new IntPtr(16), // COLOR_BTNFACE + 1
                lpszClassName = classNamePtr
            };
            RegisterClassExW(ref wc);
            _registered = true;
        }

        // Center on screen — larger dialog for multi-line editing
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        int dw = 460, dh = 320;
        int dx = (sw - dw) / 2;
        int dy = (sh - dh) / 2;

        _dialogHwnd = CreateWindowExW(0, DialogClassName, title,
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
            dx, dy, dw, dh,
            owner, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_dialogHwnd == IntPtr.Zero) return null;

        // Create multi-line edit box (set text after creation via WM_SETTEXT for proper \r\n handling)
        _editHwnd = CreateWindowExW(0x200, EditClassName, "", // WS_EX_CLIENTEDGE
            WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | WS_VSCROLL |
            ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
            12, 12, dw - 40, dh - 80,
            _dialogHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        // Set existing text — normalize line breaks to \r\n for Win32 EDIT control
        if (!string.IsNullOrEmpty(currentText))
        {
            var normalized = currentText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            SendMessageW(_editHwnd, 0x000C, IntPtr.Zero, normalized); // WM_SETTEXT
        }

        // Create OK button (id = 1) — NOT defpushbutton so Enter inserts newlines
        CreateWindowExW(0, ButtonClassName, "OK",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
            dw - 190, dh - 55, 80, 28,
            _dialogHwnd, new IntPtr(1), hInstance, IntPtr.Zero);

        // Create Cancel button (id = 2)
        CreateWindowExW(0, ButtonClassName, "Cancel",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
            dw - 100, dh - 55, 80, 28,
            _dialogHwnd, new IntPtr(2), hInstance, IntPtr.Zero);

        // Set font
        var font = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        SendMessageW(_editHwnd, WM_SETFONT, font, new IntPtr(1));

        // Set focus and place cursor at end
        SetFocus(_editHwnd);
        int textLen = (currentText ?? "").Length;
        SendMessageW(_editHwnd, 0x00B1, new IntPtr(textLen), new IntPtr(textLen)); // EM_SETSEL — cursor at end

        // Disable owner
        if (owner != IntPtr.Zero) EnableWindow(owner, 0);

        // Modal message loop
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_CLOSE || IsWindow(_dialogHwnd) == 0)
                break;

            // For multi-line edit, don't let IsDialogMessage eat Enter/Tab
            // Check if the edit has focus and message is a key
            if (msg.message == WM_KEYDOWN && msg.hwnd == _editHwnd)
            {
                // Let Enter and Tab go to the edit control directly
                int vk = (int)msg.wParam;
                if (vk == 0x0D || vk == 0x09) // VK_RETURN or VK_TAB
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                    continue;
                }
            }

            if (IsDialogMessageW(_dialogHwnd, ref msg) == 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        // Re-enable owner
        if (owner != IntPtr.Zero) EnableWindow(owner, 1);

        DeleteObject(font);
        return _result;
    }

    private static IntPtr DlgWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);

                if (id == 1) // OK
                {
                    int len = (int)SendMessageW(_editHwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
                    if (len > 0)
                    {
                        var buffer = new char[len + 1];
                        unsafe
                        {
                            fixed (char* p = buffer)
                            {
                                SendMessageW(_editHwnd, WM_GETTEXT, new IntPtr(len + 1), (IntPtr)p);
                            }
                        }
                        _result = new string(buffer, 0, len);
                    }
                    else
                    {
                        _result = "";
                    }
                    DestroyWindow(hWnd);
                    _dialogHwnd = IntPtr.Zero;
                    return IntPtr.Zero;
                }
                else if (id == 2) // Cancel
                {
                    _result = null;
                    DestroyWindow(hWnd);
                    _dialogHwnd = IntPtr.Zero;
                    return IntPtr.Zero;
                }
                break;
            }

            case WM_CLOSE:
                _result = null;
                DestroyWindow(hWnd);
                _dialogHwnd = IntPtr.Zero;
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_KEYDOWN:
                if ((int)wParam == 0x1B) // VK_ESCAPE
                {
                    _result = null;
                    DestroyWindow(hWnd);
                    _dialogHwnd = IntPtr.Zero;
                    return IntPtr.Zero;
                }
                break;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
