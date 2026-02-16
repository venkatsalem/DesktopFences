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

    // (Double-click timing handled by CS_DBLCLKS window class style)

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
                int cols = Math.Max(1, (f.Width - FenceData.IconPadding * 2) / FenceData.IconCellWidth);

                for (int i = 0; i < f.Shortcuts.Count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int ix = contentLeft + col * FenceData.IconCellWidth;
                    int iy = contentTop + row * FenceData.IconCellHeight;

                    if (mx >= ix && mx < ix + FenceData.IconCellWidth &&
                        my >= iy && my < iy + FenceData.IconCellHeight)
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
        const nuint CMD_EDIT_NOTE = 8;
        const nuint CMD_CLEAR_NOTE = 9;

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
            InsertMenuW(menu, 8, MF_SEPARATOR, 0, null);
            InsertMenuW(menu, 9, MF_STRING, CMD_EDIT_NOTE, "Edit Note...");
            if (!string.IsNullOrEmpty(hit.Fence.NoteText))
                InsertMenuW(menu, 10, MF_STRING, CMD_CLEAR_NOTE, "Clear Note");

            if (hit.Zone == HitZone.Shortcut && hit.ShortcutIndex >= 0)
            {
                InsertMenuW(menu, 11, MF_SEPARATOR, 0, null);
                InsertMenuW(menu, 12, MF_STRING, CMD_REMOVE_SHORTCUT, "Remove Shortcut");
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
            case CMD_EDIT_NOTE when hit.Fence != null:
                EditFenceNote(hit.Fence);
                break;
            case CMD_CLEAR_NOTE when hit.Fence != null:
                hit.Fence.NoteText = null;
                _saveConfig();
                _requestRedraw();
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
            // Resolve .lnk to actual target
            string resolvedPath = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveLnkTarget(path);
                if (!string.IsNullOrEmpty(target))
                    resolvedPath = target;
            }

            bool isDir = Directory.Exists(resolvedPath);
            var sc = new ShortcutItem
            {
                Name = isDir
                    ? (Path.GetFileName(resolvedPath) ?? resolvedPath)
                    : Path.GetFileNameWithoutExtension(resolvedPath),
                Target = resolvedPath,
                Type = isDir ? "folder" : "file"
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

    private void EditFenceNote(FenceData fence)
    {
        var note = NoteEditDialog.Show(_hwnd, "Edit Note — " + fence.Title, fence.NoteText);
        if (note != null)
        {
            fence.NoteText = string.IsNullOrWhiteSpace(note) ? null : note;
            _saveConfig();
            _requestRedraw();
        }
    }

    /// <summary>
    /// Resolves a .lnk shortcut file to its target path by parsing the binary format.
    /// No COM, no reflection — pure binary parsing. AOT-safe.
    /// </summary>
    private static string? ResolveLnkTarget(string lnkPath)
    {
        try
        {
            var data = File.ReadAllBytes(lnkPath);
            if (data.Length < 76) return null;

            // Verify LNK header magic: 4C 00 00 00
            if (data[0] != 0x4C || data[1] != 0x00 || data[2] != 0x00 || data[3] != 0x00)
                return null;

            // LinkFlags at offset 0x14 (4 bytes, little-endian)
            uint flags = BitConverter.ToUInt32(data, 0x14);
            bool hasLinkTargetIdList = (flags & 0x01) != 0;
            bool hasLinkInfo = (flags & 0x02) != 0;
            bool hasStringData = (flags & 0x04) != 0; // HasName
            bool hasRelativePath = (flags & 0x08) != 0; // HasRelativePath — unused here

            int offset = 76; // end of ShellLinkHeader

            // Skip LinkTargetIDList if present
            if (hasLinkTargetIdList)
            {
                if (offset + 2 > data.Length) return null;
                ushort idListSize = BitConverter.ToUInt16(data, offset);
                offset += 2 + idListSize;
            }

            // Parse LinkInfo to get the local path
            if (hasLinkInfo)
            {
                if (offset + 4 > data.Length) return null;
                uint linkInfoSize = BitConverter.ToUInt32(data, offset);
                if (linkInfoSize < 28 || offset + linkInfoSize > data.Length) return null;

                int linkInfoStart = offset;
                uint linkInfoHeaderSize = BitConverter.ToUInt32(data, linkInfoStart + 4);

                // LinkInfoFlags at offset+8
                uint linkInfoFlags = BitConverter.ToUInt32(data, linkInfoStart + 8);
                bool volumeIdAndLocalBasePath = (linkInfoFlags & 0x01) != 0;

                if (volumeIdAndLocalBasePath)
                {
                    // Try Unicode path first (available when header size >= 36)
                    if (linkInfoHeaderSize >= 36)
                    {
                        uint unicodePathOffset = BitConverter.ToUInt32(data, linkInfoStart + 28);
                        int uPathStart = linkInfoStart + (int)unicodePathOffset;
                        if (uPathStart < data.Length)
                        {
                            string uTarget = ReadNullTerminatedUnicode(data, uPathStart);
                            uTarget = ResolveGuidPath(uTarget);
                            if (!string.IsNullOrEmpty(uTarget) && (File.Exists(uTarget) || Directory.Exists(uTarget)))
                                return uTarget;
                        }
                    }

                    // Fall back to ANSI path
                    uint localBasePathOffset = BitConverter.ToUInt32(data, linkInfoStart + 16);
                    int pathStart = linkInfoStart + (int)localBasePathOffset;

                    // Read null-terminated ASCII string
                    int pathEnd = pathStart;
                    while (pathEnd < data.Length && data[pathEnd] != 0)
                        pathEnd++;

                    if (pathEnd > pathStart)
                    {
                        string target = System.Text.Encoding.Default.GetString(data, pathStart, pathEnd - pathStart);
                        target = ResolveGuidPath(target);
                        if (File.Exists(target) || Directory.Exists(target))
                            return target;
                    }
                }
            }
        }
        catch
        {
            // Failed to parse — return null, caller will use original .lnk path
        }
        return null;
    }

    /// <summary>
    /// Reads a null-terminated UTF-16LE string from a byte array.
    /// </summary>
    private static string ReadNullTerminatedUnicode(byte[] data, int offset)
    {
        int end = offset;
        while (end + 1 < data.Length)
        {
            if (data[end] == 0 && data[end + 1] == 0) break;
            end += 2;
        }
        if (end <= offset) return "";
        return System.Text.Encoding.Unicode.GetString(data, offset, end - offset);
    }

    /// <summary>
    /// Converts a Volume GUID path like "{6D809377-...}\Oracle\VirtualBox\VirtualBox.exe"
    /// to a normal drive letter path like "C:\Program Files\Oracle\VirtualBox\VirtualBox.exe".
    /// Scans all drive letters with common prefixes to find the actual file.
    /// </summary>
    private static string ResolveGuidPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Check for patterns like {GUID}\rest or \\?\Volume{GUID}\rest
        int guidStart = path.IndexOf('{');
        if (guidStart < 0) return path;
        int guidEnd = path.IndexOf('}', guidStart);
        if (guidEnd < 0) return path;

        // Extract the relative path after the GUID (skip any leading separator)
        int afterGuid = guidEnd + 1;
        if (afterGuid < path.Length && (path[afterGuid] == '\\' || path[afterGuid] == '/'))
            afterGuid++;
        if (afterGuid >= path.Length) return path;
        string remainder = path[afterGuid..];

        // Common prefixes to try (direct, Program Files, Program Files (x86), Users, etc.)
        string[] prefixes = ["", "Program Files\\", "Program Files (x86)\\"];

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string root = drive.RootDirectory.FullName;

                foreach (var prefix in prefixes)
                {
                    string candidate = Path.Combine(root, prefix + remainder);
                    if (File.Exists(candidate) || Directory.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
        }

        return path; // couldn't resolve, return original
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

    public void OnDropFiles(IntPtr hDrop)
    {
        // Get drop point (client coordinates)
        DragQueryPoint(hDrop, out var pt);

        // Find which fence the drop landed on
        var hit = HitTest(pt.X, pt.Y);
        FenceData? targetFence = hit.Fence;

        // If not dropped on a fence, use the first fence or create one
        if (targetFence == null)
        {
            if (_config.Fences.Count > 0)
                targetFence = _config.Fences[0];
            else
            {
                targetFence = new FenceData
                {
                    Title = "Dropped Items",
                    X = Math.Max(0, pt.X - 150),
                    Y = Math.Max(0, pt.Y - 20),
                };
                _config.Fences.Add(targetFence);
            }
        }

        // Query how many files were dropped
        uint fileCount = DragQueryFileW(hDrop, 0xFFFFFFFF, IntPtr.Zero, 0);

        for (uint i = 0; i < fileCount; i++)
        {
            // Get required buffer size
            uint len = DragQueryFileW(hDrop, i, IntPtr.Zero, 0);
            if (len == 0) continue;

            // Allocate buffer and get the file path
            var buffer = new char[len + 1];
            unsafe
            {
                fixed (char* p = buffer)
                {
                    DragQueryFileW(hDrop, i, (IntPtr)p, len + 1);
                }
            }
            string path = new string(buffer, 0, (int)len);

            // Resolve .lnk shortcuts to their actual target
            string resolvedPath = path;
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var target = ResolveLnkTarget(path);
                if (!string.IsNullOrEmpty(target))
                    resolvedPath = target;
            }

            // Determine type
            bool isDir = Directory.Exists(resolvedPath);
            string type = isDir ? "folder" : "file";
            string name = isDir
                ? (Path.GetFileName(resolvedPath) ?? resolvedPath)
                : Path.GetFileNameWithoutExtension(resolvedPath);

            var sc = new ShortcutItem
            {
                Name = name,
                Target = resolvedPath,
                Type = type
            };
            LoadShortcutIcon(sc);
            targetFence.Shortcuts.Add(sc);
        }

        DragFinish(hDrop);
        _saveConfig();
        _requestRedraw();
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
                // Try 48px image list for shell32 icon index 13
                var urlIcon = GetExtraLargeIcon("shell32.dll", 13);
                sc.CachedIcon = urlIcon != IntPtr.Zero ? urlIcon : ExtractIconW(IntPtr.Zero, "shell32.dll", 13);
                return;
            }

            var target = sc.Target;
            if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
            {
                // Get the system icon index for this file
                var shfi = new SHFILEINFOW();
                int result = SHGetFileInfoW(target, 0, ref shfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_SYSICONINDEX);

                if (result != 0 && shfi.iIcon >= 0)
                {
                    // Get the 48px extra-large image list — native size, no scaling
                    var iid = IID_IImageList;
                    if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IntPtr imageList) == 0 && imageList != IntPtr.Zero)
                    {
                        var icon = ImageList_GetIcon(imageList, shfi.iIcon, ILD_TRANSPARENT);
                        if (icon != IntPtr.Zero)
                        {
                            sc.CachedIcon = icon;
                            return;
                        }
                    }
                }

                // Fallback: get 32px icon via SHGetFileInfoW (will be stretched)
                shfi = new SHFILEINFOW();
                SHGetFileInfoW(target, 0, ref shfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                    SHGFI_ICON | SHGFI_LARGEICON);

                if (shfi.hIcon != IntPtr.Zero)
                {
                    sc.CachedIcon = shfi.hIcon;
                    return;
                }
            }

            // Fallback: generic file/folder icon
            var fallbackIcon = GetExtraLargeIcon("shell32.dll", sc.Type == "folder" ? 3 : 0);
            sc.CachedIcon = fallbackIcon != IntPtr.Zero
                ? fallbackIcon
                : ExtractIconW(IntPtr.Zero, "shell32.dll", sc.Type == "folder" ? 3 : 0);
        }
        catch
        {
            // Silently fail icon loading
        }
    }

    /// <summary>
    /// Gets a 48px icon from shell32.dll by index using the extra-large system image list.
    /// </summary>
    private static IntPtr GetExtraLargeIcon(string dllPath, int index)
    {
        try
        {
            // First get the system icon index for this dll icon
            var shfi = new SHFILEINFOW();
            int result = SHGetFileInfoW(dllPath, 0, ref shfi,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFOW>(),
                SHGFI_SYSICONINDEX);

            // For shell32.dll icons, the icon index from ExtractIcon maps to the
            // system image list. Use SHGetImageList directly.
            var iid = IID_IImageList;
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IntPtr imageList) == 0 && imageList != IntPtr.Zero)
            {
                if (result != 0)
                {
                    var icon = ImageList_GetIcon(imageList, shfi.iIcon, ILD_TRANSPARENT);
                    if (icon != IntPtr.Zero) return icon;
                }
            }
        }
        catch { }
        return IntPtr.Zero;
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
