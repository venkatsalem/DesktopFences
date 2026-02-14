# DesktopFences

A lightweight Windows 11 desktop organizer inspired by Stardock Fences. Built with C# (.NET 8) using raw Win32 interop — no WPF, no WinForms, no XAML. Renders via a software-rasterized 32-bit ARGB surface composited through `UpdateLayeredWindow` for true per-pixel alpha transparency.

**Target:** Windows 11 64-bit only. No backward compatibility needed.

---

## Security

This application makes **zero network connections**. No telemetry, no update checks, no analytics, no NuGet packages with network dependencies. Every operation is a local Win32 API call against the filesystem, registry, or GDI subsystem. Firewall-safe by design — can be fully blocked without any loss of function.

| Scope | What it touches |
|-------|----------------|
| Filesystem | `%APPDATA%\DesktopFences\config.json` (read/write) |
| Registry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (optional, for startup) |
| Shell | `Process.Start` with `UseShellExecute=true` — user-initiated shortcut launches only |
| Network | **None. Ever.** |

---

## Features

### Fence Management
- Create, rename, and delete fences via right-click context menu
- Drag fences by their title bar to reposition
- Resize from all 8 directions (4 edges + 4 corners, min 150x80)
- Double-click title bar to collapse/expand (indicator: `[+]` / `[-]`)
- Semi-transparent dark-themed rounded rectangles with anti-aliased edges

### Shortcut Management
- Add file shortcuts via native Open File dialog
- Add folder shortcuts (selects a file, extracts its parent directory)
- Add URL shortcuts via a pure Win32 text input dialog
- Remove individual shortcuts from the context menu
- Drag-and-drop shortcuts between fences
- Double-click any shortcut to launch it (`Process.Start` with `UseShellExecute`)
- Automatic icon loading via `SHGetFileInfoW` with shell32.dll fallbacks

### System Integration
- System tray icon with context menu (Edit Mode toggle, Start with Windows toggle, Exit)
- Always-on-bottom overlay that stays behind all other windows
- Click-through on empty areas — desktop icons and wallpaper remain accessible
- Responds to display resolution changes (`WM_DISPLAYCHANGE`)
- Single-instance enforcement via named Mutex
- Per-Monitor V2 DPI awareness

### Rendering
- Event-driven only — no timers, no polling, no idle CPU usage
- 32-bit ARGB DIB section with software-rasterized rounded rectangles
- Premultiplied-alpha text rendering (workaround for GDI's lack of alpha support)
- Batched deferred redraws via `PostMessage` to coalesce invalidations
- Composited via `UpdateLayeredWindow` for true per-pixel transparency

---

## Build

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (with AOT workload)
- Windows 11 x64

```bash
# Install .NET 8 SDK (if not installed)
winget install Microsoft.DotNet.SDK.8

# Install AOT workload
dotnet workload install aot
```

### Debug Build
```bash
dotnet build
```

### Release (AOT Single-File)
```bash
dotnet publish -c Release
```

Output: `bin/Release/net8.0-windows/win-x64/publish/DesktopFences.exe`

The AOT-compiled executable starts instantly with no JIT overhead and targets < 20MB runtime memory.

---

## Project Structure

```
DesktopFences/
  DesktopFences.csproj        .NET 8 AOT project (win-x64, no NuGet packages)
  app.manifest                DPI awareness + Windows 11 compat
  Program.cs                  Entry point, single-instance mutex

  Interop/
    NativeMethods.cs          All Win32 P/Invoke declarations (~650 lines)

  Models/
    FenceData.cs              Data models + AOT JSON source generator
    ConfigManager.cs          JSON persistence to %APPDATA%

  Rendering/
    Renderer.cs               32-bit ARGB DIB rendering + UpdateLayeredWindow

  Core/
    OverlayWindow.cs          Always-on-bottom layered window + WndProc dispatch
    InputHandler.cs           Mouse/keyboard: drag, resize, hit-testing, context menus
    TrayIcon.cs               System tray icon with edit-mode toggle
    TextInputDialog.cs        Pure Win32 modal text input dialog
    RegistryStartup.cs        HKCU Run key management
```

**10 source files, ~2,900 lines total. Zero external dependencies.**

---

## Configuration

All state is persisted to a single JSON file:

```
%APPDATA%\DesktopFences\config.json
```

Example:
```json
{
  "fences": [
    {
      "id": "a1b2c3d4",
      "title": "Applications",
      "x": 50,
      "y": 50,
      "width": 320,
      "height": 280,
      "collapsed": false,
      "shortcuts": [
        {
          "name": "Notepad",
          "target": "C:\\Windows\\notepad.exe",
          "type": "file"
        }
      ]
    }
  ],
  "editMode": true,
  "startWithWindows": false
}
```

If the config file is corrupted or missing, the app creates a fresh default with two empty fences.

---

## Usage

| Action | How |
|--------|-----|
| Create a fence | Right-click empty area > "New Fence" |
| Move a fence | Drag the title bar (edit mode) |
| Resize a fence | Drag any edge or corner (edit mode) |
| Collapse/expand | Double-click the title bar |
| Add file shortcut | Right-click fence > "Add File Shortcut..." |
| Add folder shortcut | Right-click fence > "Add Folder Shortcut..." |
| Add URL shortcut | Right-click fence > "Add URL Shortcut..." |
| Remove a shortcut | Right-click shortcut > "Remove Shortcut" |
| Move shortcut between fences | Drag it from one fence to another (edit mode) |
| Launch a shortcut | Double-click it |
| Rename a fence | Right-click fence > "Rename Fence" |
| Delete a fence | Right-click fence > "Delete Fence" |
| Toggle edit mode | System tray > "Edit Mode" (or double-click tray icon) |
| Start with Windows | System tray > "Start with Windows" |
| Exit | System tray > "Exit" |

---

## Architecture Notes

### Window Stack
The overlay is a full-screen `WS_POPUP` with `WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`. It never appears in the taskbar or Alt+Tab. `WM_WINDOWPOSCHANGING` forces `hwndInsertAfter = HWND_BOTTOM` so it always stays behind everything.

### Click-Through
Two layers of click-through protection:
1. `UpdateLayeredWindow` with per-pixel alpha — pixels with alpha=0 are inherently transparent to mouse input
2. `WM_NCHITTEST` returns `HTTRANSPARENT` when the cursor is not over any fence rectangle

### Rendering Pipeline
1. Clear 32-bit ARGB DIB to all zeros (fully transparent)
2. Software-rasterize each fence: rounded rect background, border, title bar, shortcut icons and labels
3. Fix up text alpha (GDI `TextOutW` doesn't write alpha — we detect drawn pixels and set premultiplied alpha manually)
4. Call `UpdateLayeredWindow` with `ULW_ALPHA` to composite onto the desktop

### JSON Serialization
Uses `System.Text.Json` source generators (`JsonSerializerContext`) for full AOT compatibility. No reflection-based serialization at runtime. The `JsonSerializerIsReflectionEnabledByDefault` MSBuild property is set to `false`.

---

## Design Constants

| Constant | Value | Purpose |
|----------|-------|---------|
| Title bar height | 32 px | Fence title/drag area |
| Icon size | 32 px | Shortcut icon dimensions |
| Icon cell size | 48 px | Grid cell (icon + padding) |
| Corner radius | 12 px | Rounded rectangle corners |
| Resize margin | 6 px | Hit-test zone for edge resize |
| Min fence width | 150 px | Resize constraint |
| Min fence height | 80 px | Resize constraint |
| Font | Segoe UI | System font, multiple weights |

---

## License

Private project. All rights reserved.
