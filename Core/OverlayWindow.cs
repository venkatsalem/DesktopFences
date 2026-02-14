using System.Runtime.InteropServices;
using DesktopFences.Interop;
using DesktopFences.Models;
using DesktopFences.Rendering;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Core;

/// <summary>
/// Creates and manages the always-on-bottom transparent overlay window.
/// Uses WS_EX_LAYERED with UpdateLayeredWindow for per-pixel alpha.
/// Event-driven rendering only — no timers, no polling.
/// Zero network connections.
/// </summary>
internal sealed class OverlayWindow : IDisposable
{
    private IntPtr _hwnd;
    private readonly AppConfig _config;
    private readonly Renderer _renderer;
    private InputHandler? _input;
    private TrayIcon? _tray;
    private bool _needsRedraw;
    private int _screenWidth;
    private int _screenHeight;

    // Must prevent GC of the delegate
    private readonly WndProc _wndProcDelegate;
    private static OverlayWindow? _instance;

    private const string ClassName = "DesktopFencesOverlay";

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        _renderer = new Renderer();
        _wndProcDelegate = WndProcHandler;
        _instance = this;
    }

    public void Create()
    {
        var hInstance = GetModuleHandleW(IntPtr.Zero);

        // Pin the class name string
        var classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = classNamePtr
        };

        var atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            Marshal.FreeHGlobal(classNamePtr);
            throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");
        }

        // Full-screen dimensions
        _screenWidth = GetSystemMetrics(0);  // SM_CXSCREEN
        _screenHeight = GetSystemMetrics(1); // SM_CYSCREEN

        // Create layered popup window — no taskbar presence (WS_EX_TOOLWINDOW)
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            ClassName,
            "Desktop Fences",
            WS_POPUP | WS_VISIBLE,
            0, 0, _screenWidth, _screenHeight,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
        }

        // Push to bottom of Z-order
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // Accept drag-and-drop from Explorer
        DragAcceptFiles(_hwnd, 1);

        // Sync registry state with config
        _config.StartWithWindows = RegistryStartup.IsStartWithWindows();

        // Create input handler
        _input = new InputHandler(_hwnd, _config, RequestRedraw, SaveConfig);

        // Create tray icon
        _tray = new TrayIcon(_hwnd, _config, RequestRedraw, SaveConfig, ExitApp);
        _tray.Create();

        // Load icons for existing shortcuts
        _input.LoadAllIcons();

        // Initialize renderer and do first draw
        _renderer.EnsureTarget(_hwnd, _screenWidth, _screenHeight);
        Redraw();

        ShowWindow(_hwnd, 8); // SW_SHOWNA — show without activating
    }

    public void RunMessageLoop()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
            {
                // We use UpdateLayeredWindow, so WM_PAINT is a no-op.
                // Must still call BeginPaint/EndPaint to validate the region.
                BeginPaint(hWnd, out var ps);
                EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            }

            case WM_ERASEBKGND:
                return new IntPtr(1); // Handled

            case WM_MOUSEMOVE:
                _input?.OnMouseMove(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                _input?.OnLButtonDown(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                _input?.OnLButtonUp(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case WM_LBUTTONDBLCLK:
                _input?.OnLButtonDblClk(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                _input?.OnRButtonUp(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                return IntPtr.Zero;

            case WM_KEYDOWN:
                _input?.OnKeyDown((int)wParam);
                return IntPtr.Zero;

            case WM_CHAR:
                _input?.OnChar((char)wParam);
                return IntPtr.Zero;

            case WM_SETCURSOR:
                // Let input handler manage cursors during interaction
                if (LOWORD(lParam) == 1) // HTCLIENT
                {
                    return new IntPtr(1); // We set the cursor in OnMouseMove
                }
                break;

            case WM_WINDOWPOSCHANGING:
            {
                // Force the window to stay at HWND_BOTTOM always
                unsafe
                {
                    var pos = (WINDOWPOS*)lParam;
                    pos->hwndInsertAfter = HWND_BOTTOM;
                }
                return IntPtr.Zero;
            }

            case WM_NCHITTEST:
            {
                // For layered windows with UpdateLayeredWindow, transparent pixels
                // (alpha=0) are automatically click-through. But we still handle
                // NCHITTEST so the window doesn't steal focus from desktop items.
                // We check whether the cursor is over a fence — if not, pass through.
                int mx = GET_X_LPARAM(lParam);
                int my = GET_Y_LPARAM(lParam);
                // lParam is screen coords; convert to client
                var pt = new POINT { X = mx, Y = my };
                ScreenToClient(hWnd, ref pt);

                if (_input != null)
                {
                    var hit = _input.HitTest(pt.X, pt.Y);
                    if (hit.Fence == null)
                    {
                        // Nothing under cursor — let clicks pass through
                        return new IntPtr(-1); // HTTRANSPARENT
                    }
                }
                return new IntPtr(1); // HTCLIENT
            }

            case WM_DISPLAYCHANGE:
            {
                // Screen resolution changed — resize overlay
                _screenWidth = GetSystemMetrics(0);
                _screenHeight = GetSystemMetrics(1);
                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, _screenWidth, _screenHeight,
                    SWP_NOACTIVATE);
                _renderer.EnsureTarget(_hwnd, _screenWidth, _screenHeight);
                Redraw();
                return IntPtr.Zero;
            }

            case WM_DROPFILES:
                _input?.OnDropFiles(wParam); // wParam is HDROP
                return IntPtr.Zero;

            case WM_TRAYICON:
                _tray?.HandleMessage(wParam, lParam);
                return IntPtr.Zero;

            case WM_USER + 2:
                // Deferred redraw — batched from RequestRedraw()
                if (_needsRedraw)
                    Redraw();
                return IntPtr.Zero;

            case WM_DESTROY:
                _tray?.Dispose();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void RequestRedraw()
    {
        if (!_needsRedraw)
        {
            _needsRedraw = true;
            // Post a custom message to defer rendering to the message loop
            // This batches multiple invalidations into one render pass
            PostMessageW(_hwnd, WM_USER + 2, IntPtr.Zero, IntPtr.Zero);
        }

        // Also handle the deferred redraw inline if we just posted it
        // Actually, let's check for our custom message in the wndproc
    }

    private void Redraw()
    {
        _needsRedraw = false;
        _renderer.Render(_hwnd, _config,
            _input?.CurrentHover,
            _input?.EditingFence,
            _input?.EditText,
            _input?.EditCursorPos ?? 0);
    }

    private void SaveConfig()
    {
        ConfigManager.Save(_config);
    }

    private void ExitApp()
    {
        SaveConfig();
        DestroyWindow(_hwnd);
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _renderer.Dispose();
    }
}
