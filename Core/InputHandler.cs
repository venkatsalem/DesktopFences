using DesktopFences.Interop;
using DesktopFences.Models;
using DesktopFences.Rendering;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Core;

/// <summary>
/// Handles all mouse and keyboard interaction with fences.
/// </summary>
internal sealed class InputHandler
{
    private readonly AppConfig _config;
    private readonly Action _requestRedraw;
    private readonly Action _saveConfig;
    private readonly IntPtr _hwnd;

    // Drag state
    private bool _isDragging;
    private DragMode _dragMode;
    private FenceData? _dragFence;
    private int _dragStartX, _dragStartY;
    private int _dragFenceStartX, _dragFenceStartY;
    private int _dragFenceStartW, _dragFenceStartH;

    // Shortcut drag
    private bool _isDraggingShortcut;
    private FenceData? _shortcutSourceFence;
    private int _shortcutSourceIndex = -1;
    private int _shortcutDragX, _shortcutDragY;

    // Title editing
    private bool _isEditingTitle;
    private FenceData? _editingFence;
    private string _editText = "";
    private int _editCursorPos;

    // Hover tracking
    private HitTestInfo? _currentHover;

    // Double-click timing
    private DateTime _lastClickTime;
    private int _lastClickX, _lastClickY;

    public HitTestInfo? CurrentHover => _currentHover;
    public FenceData? EditingFence => _isEditingTitle ? _editingFence : null;
    public string? EditText => _isEditingTitle ? _editText : null;
    public int EditCursorPos => _editCursorPos;

    private enum DragMode
    {
        None, Move,
        ResizeLeft, ResizeRight, ResizeTop, ResizeBottom,
        ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight
    }

    public InputHandler(IntPtr hwnd, AppConfig config, Action requestRedraw, Action saveConfig)
    {
        _hwnd = hwnd;
        _config = config;
        _requestRedraw = requestRedraw;
        _saveConfig = saveConfig;
    }

    public HitTestInfo HitTest(int mx, int my)
    {
        var result = new HitTestInfo();
        const int resizeMargin = 6;

        // Iterate in reverse so top-drawn fences get priority
        for (int fi = _config.Fences.Count - 1; fi >= 0; fi--)
        {
            var f = _config.Fences[fi];
            int dh = f.DisplayHeight;

            if (mx < f.X || mx > f.X + f.Width || my < f.Y || my > f.Y + dh)
                continue;

            result.Fence = f;

            // Check resize zones (only in edit mode)
            if (_config.EditMode)
            {
                bool left = mx - f.X < resizeMargin;
                bool right = f.X + f.Width - mx < resizeMargin;
                bool top = my - f.Y < resizeMargin;
                bool bottom = f.Y + dh - my < resizeMargin;

                if (top && left) { result.Zone = HitZone.ResizeTopLeft; return result; }
                if (top && right) { result.Zone = HitZone.ResizeTopRight; return result; }
                if (bottom && left) { result.Zone = HitZone.ResizeBottomLeft; return result; }
                if (bottom && right) { result.Zone = HitZone.ResizeBottomRight; return result; }
                if (left) { result.Zone = HitZone.ResizeLeft; return result; }
                if (right) { result.Zone = HitZone.ResizeRight; return result; }
                if (top) { result.Zone = HitZone.ResizeTop; return result; }
                if (bottom && !f.Collapsed) { result.Zone = HitZone.ResizeBottom; return result; }
            }

            // Title bar
            if (my < f.Y + FenceData.TitleBarHeight)
            {
                result.Zone = HitZone.TitleBar;
                return result;
            }

            // Shortcut hit test
            if (!f.Collapsed)
            {
                int contentTop = f.Y + FenceData.TitleBarHeight + 4;
                int contentLeft = f.X + FenceData.IconPadding;
                int cols = Math.Max(1, (f.Width - FenceData.IconPadding * 2) / FenceData.IconCellSize);

                for (int i = 0; i < f.Shortcuts.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int ix = contentLeft + col * FenceData.IconCellSize;
                    int iy = contentTop + row * FenceData.IconCellSize;

                    if (mx >= ix && mx < ix + FenceData.IconCellSize &&
                        my >= iy && my < iy + FenceData.IconCellSize)
                    {
                        result.Zone = HitZone.Shortcut;
                        result.ShortcutIndex = i;
                        return result;
                    }
                }
            }

            result.Zone = HitZone.Body;
            return result;
        }

        return result;
    }

    public void OnMouseMove(int mx, int my)
    {
        if (_isDragging && _dragFence != null)
        {
            int dx = mx - _dragStartX;
            int dy = my - _dragStartY;

            switch (_dragMode)
            {
                case DragMode.Move:
                    _dragFence.X = _dragFenceStartX + dx;
                    _dragFence.Y = _dragFenceStartY + dy;
                    break;

                case DragMode.ResizeRight:
                    _dragFence.Width = Math.Max(150, _dragFenceStartW + dx);
                    if (!_dragFence.Collapsed) _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeBottom:
                    _dragFence.Height = Math.Max(80, _dragFenceStartH + dy);
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeLeft:
                    int newW = Math.Max(150, _dragFenceStartW - dx);
                    _dragFence.X = _dragFenceStartX + _dragFenceStartW - newW;
                    _dragFence.Width = newW;
                    break;

                case DragMode.ResizeTop:
                    int newH = Math.Max(80, _dragFenceStartH - dy);
                    _dragFence.Y = _dragFenceStartY + _dragFenceStartH - newH;
                    _dragFence.Height = newH;
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeBottomRight:
                    _dragFence.Width = Math.Max(150, _dragFenceStartW + dx);
                    _dragFence.Height = Math.Max(80, _dragFenceStartH + dy);
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeTopLeft:
                    int nw2 = Math.Max(150, _dragFenceStartW - dx);
                    int nh2 = Math.Max(80, _dragFenceStartH - dy);
                    _dragFence.X = _dragFenceStartX + _dragFenceStartW - nw2;
                    _dragFence.Y = _dragFenceStartY + _dragFenceStartH - nh2;
                    _dragFence.Width = nw2;
                    _dragFence.Height = nh2;
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeTopRight:
                    _dragFence.Width = Math.Max(150, _dragFenceStartW + dx);
                    int nh3 = Math.Max(80, _dragFenceStartH - dy);
                    _dragFence.Y = _dragFenceStartY + _dragFenceStartH - nh3;
                    _dragFence.Height = nh3;
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;

                case DragMode.ResizeBottomLeft:
                    int nw4 = Math.Max(150, _dragFenceStartW - dx);
                    _dragFence.X = _dragFenceStartX + _dragFenceStartW - nw4;
                    _dragFence.Width = nw4;
                    _dragFence.Height = Math.Max(80, _dragFenceStartH + dy);
                    _dragFence.ExpandedHeight = _dragFence.Height;
                    break;
            }

            _requestRedraw();
            return;
        }

        if (_isDraggingShortcut)
        {
            _shortcutDragX = mx;
            _shortcutDragY = my;
            _requestRedraw();
            return;
        }

        // Update hover
        var newHover = HitTest(mx, my);
        bool changed = _currentHover?.Fence != newHover.Fence ||
                       _currentHover?.Zone != newHover.Zone ||
                       _currentHover?.ShortcutIndex != newHover.ShortcutIndex;

        _currentHover = newHover;

        // Update cursor
        UpdateCursor(newHover);

        if (changed) _requestRedraw();
    }

    public void OnLButtonDown(int mx, int my)
    {
        // End title editing if clicking elsewhere
        if (_isEditingTitle)
        {
            CommitTitleEdit();
        }

        var hit = HitTest(mx, my);
        if (hit.Fence == null) return;

        if (_config.EditMode)
        {
            var mode = hit.Zone switch
            {
                HitZone.TitleBar => DragMode.Move,
                HitZone.ResizeLeft => DragMode.ResizeLeft,
                HitZone.ResizeRight => DragMode.ResizeRight,
                HitZone.ResizeTop => DragMode.ResizeTop,
                HitZone.ResizeBottom => DragMode.ResizeBottom,
                HitZone.ResizeTopLeft => DragMode.ResizeTopLeft,
                HitZone.ResizeTopRight => DragMode.ResizeTopRight,
                HitZone.ResizeBottomLeft => DragMode.ResizeBottomLeft,
                HitZone.ResizeBottomRight => DragMode.ResizeBottomRight,
                _ => DragMode.None
            };

            if (mode != DragMode.None)
            {
                _isDragging = true;
                _dragMode = mode;
                _dragFence = hit.Fence;
                _dragStartX = mx;
                _dragStartY = my;
                _dragFenceStartX = hit.Fence.X;
                _dragFenceStartY = hit.Fence.Y;
                _dragFenceStartW = hit.Fence.Width;
                _dragFenceStartH = hit.Fence.Height;
                SetCapture(_hwnd);
                return;
            }

            // Start shortcut drag
            if (hit.Zone == HitZone.Shortcut && hit.ShortcutIndex >= 0)
            {
                _isDraggingShortcut = true;
                _shortcutSourceFence = hit.Fence;
                _shortcutSourceIndex = hit.ShortcutIndex;
                _shortcutDragX = mx;
                _shortcutDragY = my;
                SetCapture(_hwnd);
                return;
            }
        }
        else
        {
            // In non-edit mode, still allow shortcut interaction
            if (hit.Zone == HitZone.Shortcut && hit.ShortcutIndex >= 0)
            {
                // Will be handled by double-click
            }
        }
    }

    public void OnLButtonUp(int mx, int my)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _dragFence = null;
            ReleaseCapture();
            _saveConfig();
            return;
        }

        if (_isDraggingShortcut && _shortcutSourceFence != null && _shortcutSourceIndex >= 0)
        {
            _isDraggingShortcut = false;
            ReleaseCapture();

            // Find target fence
            var hit = HitTest(mx, my);
            if (hit.Fence != null && hit.Fence != _shortcutSourceFence)
            {
                // Move shortcut to target fence
                var sc = _shortcutSourceFence.Shortcuts[_shortcutSourceIndex];
                _shortcutSourceFence.Shortcuts.RemoveAt(_shortcutSourceIndex);
                hit.Fence.Shortcuts.Add(sc);
                _saveConfig();
            }

            _shortcutSourceFence = null;
            _shortcutSourceIndex = -1;
            _requestRedraw();
            return;
        }
    }

    public void OnLButtonDblClk(int mx, int my)
    {
        var hit = HitTest(mx, my);
        if (hit.Fence == null) return;

        if (hit.Zone == HitZone.TitleBar)
        {
            // Toggle collapse
            if (hit.Fence.Collapsed)
            {
                hit.Fence.Collapsed = false;
                hit.Fence.Height = hit.Fence.ExpandedHeight;
            }
            else
            {
                hit.Fence.ExpandedHeight = hit.Fence.Height;
                hit.Fence.Collapsed = true;
            }
            _saveConfig();
            _requestRedraw();
            return;
        }

        if (hit.Zone == HitZone.Shortcut && hit.ShortcutIndex >= 0)
        {
            // Launch shortcut
            var sc = hit.Fence.Shortcuts[hit.ShortcutIndex];
            LaunchShortcut(sc);
        }
    }

    public void OnRButtonUp(int mx, int my)
    {
        var hit = HitTest(mx, my);

        // Get screen coordinates
        GetCursorPos(out var pt);

        var menu = CreatePopupMenu();

        const nuint CMD_NEW_FENCE = 1;
        const nuint CMD_RENAME = 2;
        const nuint CMD_DELETE = 3;
        const nuint CMD_ADD_SHORTCUT = 4;
        const nuint CMD_ADD_FOLDER = 5;
        const nuint CMD_ADD_URL = 6;
        const nuint CMD_REMOVE_SHORTCUT = 7;

        InsertMenuW(menu, 0, MF_STRING, CMD_NEW_FENCE, "New Fence");
        InsertMenuW(menu, 1, MF_SEPARATOR, 0, null);

        if (hit.Fence != null)
        {
            InsertMenuW(menu, 2, MF_STRING, CMD_RENAME, "Rename Fence");
            InsertMenuW(menu, 3, MF_STRING, CMD_DELETE, "Delete Fence");
            InsertMenuW(menu, 4, MF_SEPARATOR, 0, null);
            InsertMenuW(menu, 5, MF_STRING, CMD_ADD_SHORTCUT, "Add File Shortcut...");
            InsertMenuW(menu, 6, MF_STRING, CMD_ADD_FOLDER, "Add Folder Shortcut...");
            InsertMenuW(menu, 7, MF_STRING, CMD_ADD_URL, "Add URL Shortcut...");

            if (hit.Zone == HitZone.Shortcut && hit.ShortcutIndex >= 0)
            {
                InsertMenuW(menu, 8, MF_SEPARATOR, 0, null);
                InsertMenuW(menu, 9, MF_STRING, CMD_REMOVE_SHORTCUT, "Remove Shortcut");
            }
        }

        SetForegroundWindow(_hwnd);
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        PostMessageW(_hwnd, 0, IntPtr.Zero, IntPtr.Zero); // dismiss

        switch ((nuint)cmd)
        {
            case CMD_NEW_FENCE:
                CreateNewFence(mx, my);
                break;
            case CMD_RENAME when hit.Fence != null:
                StartTitleEdit(hit.Fence);
                break;
            case CMD_DELETE when hit.Fence != null:
                _config.Fences.Remove(hit.Fence);
                // Destroy cached icons
                foreach (var sc in hit.Fence.Shortcuts)
                {
                    if (sc.CachedIcon != IntPtr.Zero)
                        DestroyIcon(sc.CachedIcon);
                }
                _saveConfig();
                _requestRedraw();
                break;
            case CMD_ADD_SHORTCUT when hit.Fence != null:
                AddFileShortcut(hit.Fence);
                break;
            case CMD_ADD_FOLDER when hit.Fence != null:
                AddFolderShortcut(hit.Fence);
                break;
            case CMD_ADD_URL when hit.Fence != null:
                AddUrlShortcut(hit.Fence);
                break;
            case CMD_REMOVE_SHORTCUT when hit.Fence != null && hit.ShortcutIndex >= 0:
                var removedSc = hit.Fence.Shortcuts[hit.ShortcutIndex];
                if (removedSc.CachedIcon != IntPtr.Zero)
                    DestroyIcon(removedSc.CachedIcon);
                hit.Fence.Shortcuts.RemoveAt(hit.ShortcutIndex);
                _saveConfig();
                _requestRedraw();
                break;
        }
    }

    public void OnKeyDown(int vk)
    {
        if (!_isEditingTitle) return;

        switch (vk)
        {
            case 0x0D: // VK_RETURN
                CommitTitleEdit();
                break;
            case 0x1B: // VK_ESCAPE
                CancelTitleEdit();
                break;
            case 0x08: // VK_BACK
                if (_editCursorPos > 0)
                {
                    _editText = _editText.Remove(_editCursorPos - 1, 1);
                    _editCursorPos--;
                    _requestRedraw();
                }
                break;
            case 0x2E: // VK_DELETE
                if (_editCursorPos < _editText.Length)
                {
                    _editText = _editText.Remove(_editCursorPos, 1);
                    _requestRedraw();
                }
                break;
            case 0x25: // VK_LEFT
                if (_editCursorPos > 0) { _editCursorPos--; _requestRedraw(); }
                break;
            case 0x27: // VK_RIGHT
                if (_editCursorPos < _editText.Length) { _editCursorPos++; _requestRedraw(); }
                break;
        }
    }

    public void OnChar(char c)
    {
        if (!_isEditingTitle) return;
        if (c < 32) return; // control chars

        _editText = _editText.Insert(_editCursorPos, c.ToString());
        _editCursorPos++;
        _requestRedraw();
    }

    private void CreateNewFence(int mx, int my)
    {
        var fence = new FenceData
        {
            Title = "New Fence",
            X = mx - 50,
            Y = my - 20,
            Width = 300,
            Height = 250,
            ExpandedHeight = 250
        };
        _config.Fences.Add(fence);
        _saveConfig();
        _requestRedraw();

        // Start editing the title immediately
        StartTitleEdit(fence);
    }

    private void StartTitleEdit(FenceData fence)
    {
        _isEditingTitle = true;
        _editingFence = fence;
        _editText = fence.Title;
        _editCursorPos = _editText.Length;
        _requestRedraw();
    }

    private void CommitTitleEdit()
    {
        if (_editingFence != null && !string.IsNullOrWhiteSpace(_editText))
        {
            _editingFence.Title = _editText.Trim();
        }
        _isEditingTitle = false;
        _editingFence = null;
        _editText = "";
        _saveConfig();
        _requestRedraw();
    }

    private void CancelTitleEdit()
    {
        _isEditingTitle = false;
        _editingFence = null;
        _editText = "";
        _requestRedraw();
    }

    private void AddFileShortcut(FenceData fence)
    {
        var path = ShowOpenFileDialog("All Files\0*.*\0Executables\0*.exe;*.lnk\0\0");
        if (path != null)
        {
            var sc = new ShortcutItem
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Target = path,
                Type = "file"
            };
            LoadShortcutIcon(sc);
            fence.Shortcuts.Add(sc);
            _saveConfig();
            _requestRedraw();
        }
    }

    private void AddFolderShortcut(FenceData fence)
    {
        // Use a simple folder browser - we'll use SHBrowseForFolder or just a known path
        // For simplicity, use the open file dialog and extract the directory
        var path = ShowOpenFileDialog("All Files\0*.*\0\0");
        if (path != null)
        {
            var dir = Path.GetDirectoryName(path) ?? path;
            var sc = new ShortcutItem
            {
                Name = Path.GetFileName(dir) ?? dir,
                Target = dir,
                Type = "folder"
            };
            LoadShortcutIcon(sc);
            fence.Shortcuts.Add(sc);
            _saveConfig();
            _requestRedraw();
        }
    }

    private void AddUrlShortcut(FenceData fence)
    {
        var url = TextInputDialog.Show(_hwnd, "Add URL Shortcut", "Enter URL:", "https://");
        if (!string.IsNullOrWhiteSpace(url))
        {
            var sc = new ShortcutItem
            {
                Name = GetUrlDisplayName(url),
                Target = url,
                Type = "url"
            };
            LoadShortcutIcon(sc);
            fence.Shortcuts.Add(sc);
            _saveConfig();
            _requestRedraw();
        }
    }

    private static string GetUrlDisplayName(string url)
    {
        try
        {
            // Extract domain name for display — pure string parsing, no network
            var trimmed = url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
            var slash = trimmed.IndexOf('/');
            return slash > 0 ? trimmed[..slash] : trimmed;
        }
        catch
        {
            return "Web Link";
        }
    }

    public void LoadAllIcons()
    {
        foreach (var fence in _config.Fences)
        {
            foreach (var sc in fence.Shortcuts)
            {
                LoadShortcutIcon(sc);
            }
        }
    }

    public static void LoadShortcutIcon(ShortcutItem sc)
    {
        if (sc.CachedIcon != IntPtr.Zero) return;

        try
        {
            if (sc.Type == "url")
            {
                // Use shell32 default icon for URLs
                sc.CachedIcon = ExtractIconW(IntPtr.Zero, "shell32.dll", 13);
                return;
            }

            var target = sc.Target;
            if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
            {
                var shfi = new SHFILEINFOW();
                SHGetFileInfoW(target, 0, ref shfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_ICON | SHGFI_LARGEICON);

                if (shfi.hIcon != IntPtr.Zero)
                {
                    sc.CachedIcon = shfi.hIcon;
                    return;
                }
            }

            // Fallback: generic file icon
            sc.CachedIcon = ExtractIconW(IntPtr.Zero, "shell32.dll",
                sc.Type == "folder" ? 3 : 0);
        }
        catch
        {
            // Silently fail icon loading
        }
    }

    private static void LaunchShortcut(ShortcutItem sc)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = sc.Target,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Failed to launch — silently ignore
        }
    }

    private unsafe string? ShowOpenFileDialog(string filter)
    {
        var fileBuffer = new char[260];
        fixed (char* pFilter = filter)
        fixed (char* pFile = fileBuffer)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = System.Runtime.InteropServices.Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = _hwnd,
                lpstrFilter = (IntPtr)pFilter,
                lpstrFile = (IntPtr)pFile,
                nMaxFile = 260,
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };

            if (GetOpenFileNameW(ref ofn))
            {
                return new string(pFile).TrimEnd('\0');
            }
        }
        return null;
    }

    private void UpdateCursor(HitTestInfo hit)
    {
        IntPtr cursor = hit.Zone switch
        {
            HitZone.ResizeLeft or HitZone.ResizeRight => LoadCursorW(IntPtr.Zero, IDC_SIZEWE),
            HitZone.ResizeTop or HitZone.ResizeBottom => LoadCursorW(IntPtr.Zero, IDC_SIZENS),
            HitZone.ResizeTopLeft or HitZone.ResizeBottomRight => LoadCursorW(IntPtr.Zero, IDC_SIZENWSE),
            HitZone.ResizeTopRight or HitZone.ResizeBottomLeft => LoadCursorW(IntPtr.Zero, IDC_SIZENESW),
            HitZone.Shortcut => LoadCursorW(IntPtr.Zero, IDC_HAND),
            HitZone.TitleBar when _isEditingTitle && _editingFence == hit.Fence => LoadCursorW(IntPtr.Zero, IDC_IBEAM),
            _ => LoadCursorW(IntPtr.Zero, IDC_ARROW)
        };
        SetCursor(cursor);
    }
}
