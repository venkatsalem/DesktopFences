using System.Runtime.InteropServices;
using DesktopFences.Interop;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Core;

/// <summary>
/// A minimal Win32 text input dialog using raw CreateWindowEx.
/// No WinForms, no WPF, no XAML. Pure P/Invoke.
/// Zero network connections.
/// </summary>
internal static class TextInputDialog
{
    private const string DialogClassName = "DFTextInputDlg";
    private const string EditClassName = "EDIT";
    private const string ButtonClassName = "BUTTON";

    private static string? _result;
    private static IntPtr _editHwnd;
    private static IntPtr _dialogHwnd;
    private static bool _registered;
    private static WndProc? _dlgProc;

    // Edit control styles
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_TABSTOP = 0x00010000;
    private const uint ES_AUTOHSCROLL = 0x0080;
    private const uint BS_PUSHBUTTON = 0x0000;
    private const uint BS_DEFPUSHBUTTON = 0x0001;

    // Window messages
    private const uint WM_SETFONT = 0x0030;
    private const uint WM_GETTEXT = 0x000D;
    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WM_SETTEXT = 0x000C;
    private const uint BN_CLICKED = 0;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_SYSMENU = 0x00080000;
    private const uint WS_OVERLAPPED = 0x00000000;

    // SendMessage
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, string? lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnableWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bEnable);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsDialogMessageW(IntPtr hDlg, ref MSG lpMsg);

    public static string? Show(IntPtr owner, string title, string prompt, string defaultValue = "")
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

        // Center on screen
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);
        int dw = 400, dh = 160;
        int dx = (sw - dw) / 2;
        int dy = (sh - dh) / 2;

        _dialogHwnd = CreateWindowExW(0, DialogClassName, title,
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
            dx, dy, dw, dh,
            owner, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_dialogHwnd == IntPtr.Zero) return null;

        // Create prompt label (STATIC)
        var lblHwnd = CreateWindowExW(0, "STATIC", prompt,
            WS_CHILD | WS_VISIBLE,
            12, 12, dw - 40, 20,
            _dialogHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        // Create edit box
        _editHwnd = CreateWindowExW(0x200, EditClassName, defaultValue, // WS_EX_CLIENTEDGE
            WS_CHILD | WS_VISIBLE | WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL,
            12, 38, dw - 40, 24,
            _dialogHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        // Create OK button (id = 1)
        CreateWindowExW(0, ButtonClassName, "OK",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON,
            dw - 190, dh - 65, 80, 28,
            _dialogHwnd, new IntPtr(1), hInstance, IntPtr.Zero);

        // Create Cancel button (id = 2)
        CreateWindowExW(0, ButtonClassName, "Cancel",
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
            dw - 100, dh - 65, 80, 28,
            _dialogHwnd, new IntPtr(2), hInstance, IntPtr.Zero);

        // Set font on all controls
        var font = CreateFontW(-14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        SendMessageW(lblHwnd, WM_SETFONT, font, new IntPtr(1));
        SendMessageW(_editHwnd, WM_SETFONT, font, new IntPtr(1));

        // Set focus and select text
        SetFocus(_editHwnd);
        SendMessageW(_editHwnd, 0x00B1, IntPtr.Zero, new IntPtr(-1)); // EM_SETSEL

        // Disable owner
        if (owner != IntPtr.Zero) EnableWindow(owner, false);

        // Modal message loop
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            if (msg.message == WM_CLOSE || !IsWindow(_dialogHwnd))
                break;

            if (!IsDialogMessageW(_dialogHwnd, ref msg))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        // Re-enable owner
        if (owner != IntPtr.Zero) EnableWindow(owner, true);

        DeleteObject(font);
        return _result;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    private static IntPtr DlgWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
            {
                int id = (int)(wParam.ToInt64() & 0xFFFF);

                if (id == 1) // OK
                {
                    // Get text from edit
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
