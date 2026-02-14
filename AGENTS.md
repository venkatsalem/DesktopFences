# AGENTS.md — DesktopFences Coding Guidelines

This document defines the rules, conventions, and architectural constraints for any agent (AI or human) modifying the DesktopFences codebase.

---

## Critical Constraints

### Security: Zero Network Access
**This is a non-negotiable hard rule.** The application must never make any network connection under any circumstance.

- **No HTTP/HTTPS calls.** No `HttpClient`, no `WebRequest`, no `WebClient`, no sockets.
- **No NuGet packages** that perform network operations, phone home, or include telemetry.
- **No telemetry, analytics, crash reporting, or update checks.**
- **No DNS resolution.** Not even to check connectivity.
- The only permitted external process launch is `Process.Start` with `UseShellExecute = true` for user-initiated shortcut activation. The app itself opens no connections.
- When parsing URLs (for display names, etc.), use **string operations only** — never `Uri.Host` with resolution, never `Dns.GetHostEntry`, never any network call.
- If a proposed change introduces any dependency or code path that could make a network connection, **reject it**.

### No XAML Frameworks
- No WPF, WinUI, MAUI, UWP, or Avalonia.
- No WinForms (`System.Windows.Forms`).
- All windowing is done through P/Invoke against user32.dll, gdi32.dll, shell32.dll, dwmapi.dll, etc.
- If you need a new dialog, build it from `CreateWindowExW` (see `TextInputDialog.cs` for the pattern).

### AOT Compatibility
- The project compiles with `PublishAot = true`. All code must be AOT-safe.
- **No reflection-based serialization.** JSON uses `System.Text.Json` source generators (`AppJsonContext`).
- **No `Type.GetType()`, `Activator.CreateInstance()`, or `Assembly.Load()`.**
- **No `dynamic` keyword.**
- `Marshal.GetFunctionPointerForDelegate` is permitted for Win32 callbacks — ensure the delegate instance is stored in a field to prevent GC.
- When adding new serializable types, register them in `AppJsonContext` in `Models/FenceData.cs`.

### Memory Budget
- Target < 20MB runtime working set.
- The main memory consumer is the full-screen 32-bit ARGB DIB (`width * height * 4` bytes). On a 1920x1080 display, this is ~8MB.
- Avoid allocating large managed arrays in hot paths. Use `stackalloc` for small buffers (< 4KB), heap arrays for larger ones.
- Destroy all GDI objects (`DeleteObject`, `DeleteDC`, `DestroyIcon`) when no longer needed.
- Cache shell icons in `ShortcutItem.CachedIcon` — load once, reuse across renders.

---

## Architecture

### Layer Diagram
```
Program.cs                    Entry point + single-instance guard
    |
    v
OverlayWindow.cs              WndProc message dispatch + window lifecycle
    |
    +---> InputHandler.cs      Mouse/keyboard state machine, hit-testing
    +---> Renderer.cs          Software rendering to ARGB DIB
    +---> TrayIcon.cs          System tray integration
    +---> ConfigManager.cs     JSON persistence
    +---> RegistryStartup.cs   Startup registry key
    +---> TextInputDialog.cs   Modal text input (pure Win32)
```

### Data Flow
1. **Input** arrives as Win32 messages in `OverlayWindow.WndProcHandler`
2. Messages are routed to `InputHandler` methods (`OnMouseMove`, `OnLButtonDown`, etc.)
3. `InputHandler` mutates `AppConfig` (fence positions, shortcuts, etc.) and calls `RequestRedraw()`
4. `RequestRedraw()` posts `WM_USER+2` to batch redraws
5. On `WM_USER+2`, `Renderer.Render()` draws all fences to the DIB and calls `UpdateLayeredWindow`
6. `ConfigManager.Save()` writes the current state to JSON on every meaningful change

### Rendering Pipeline
```
ClearBitmap()           Zero all pixels (fully transparent)
    |
    v
For each fence:
    DrawRoundedRectFilled()     Background (alpha composited)
    DrawRoundedRectOutline()    Border
    DrawRoundedRectFilled()     Title bar
    DrawAlphaText()             Title text (with alpha fixup)
    DrawHLine()                 Separator
    For each shortcut:
        DrawIconEx()            Shell icon
        DrawAlphaText()         Truncated label
    |
    v
UpdateLayeredWindow()   Composite DIB onto desktop with per-pixel alpha
```

### Hit-Testing
`InputHandler.HitTest(mx, my)` walks fences in reverse order (top-drawn first) and returns a `HitTestInfo` with:
- `Fence` — which fence was hit (null if empty space)
- `Zone` — one of: `TitleBar`, `Body`, `Shortcut`, `ResizeLeft/Right/Top/Bottom`, `ResizeTopLeft/TopRight/BottomLeft/BottomRight`
- `ShortcutIndex` — index into `fence.Shortcuts` if zone is `Shortcut`

The same hit-test is used for cursor updates, mouse interaction, and `WM_NCHITTEST` click-through logic.

---

## File Responsibilities

| File | Responsibility | May modify |
|------|---------------|------------|
| `NativeMethods.cs` | P/Invoke declarations only | Never modifies state |
| `FenceData.cs` | Data model definitions + JSON context | Schema only |
| `ConfigManager.cs` | Load/save JSON config | Filesystem (AppData) |
| `OverlayWindow.cs` | Window lifecycle + message routing | Window handle, renderer |
| `InputHandler.cs` | Input state machine | AppConfig (fences, shortcuts) |
| `Renderer.cs` | Pixel-level rendering | DIB bitmap memory |
| `TrayIcon.cs` | Tray icon + tray menu | Tray notification data |
| `TextInputDialog.cs` | Modal text input | Dialog window handles |
| `RegistryStartup.cs` | Startup registry key | HKCU Run key |
| `Program.cs` | Entry point | Nothing directly |

---

## Coding Conventions

### Naming
- **Classes:** PascalCase (`OverlayWindow`, `InputHandler`)
- **Methods:** PascalCase (`OnMouseMove`, `DrawFence`)
- **Private fields:** `_camelCase` with underscore prefix (`_hwnd`, `_isDragging`)
- **Constants:** PascalCase in classes (`TitleBarHeight`), UPPER_CASE in interop (`WM_PAINT`, `SWP_NOMOVE`)
- **Local variables:** camelCase (`mx`, `fence`, `hit`)

### Interop
- All P/Invoke goes in `Interop/NativeMethods.cs`. No scattered `DllImport` or `LibraryImport` in other files.
  - **Exception:** `TextInputDialog.cs` has private `SendMessageW` overloads because it needs unique signatures not shared elsewhere.
- Use `LibraryImport` (not `DllImport`) for source-generator-based marshalling (AOT requirement).
- Use `StringMarshalling.Utf16` for all string parameters in Win32 calls.
- All interop structs use `LayoutKind.Sequential` and live in `NativeMethods`.
- Handle cleanup: every `CreateDIBSection` needs a `DeleteObject`, every `CreateCompatibleDC` needs a `DeleteDC`, every `SHGetFileInfoW` icon needs a `DestroyIcon` on disposal.

### Error Handling
- Win32 interop failures: fail silently or log to debug output. Never crash.
- Config load failures: fall back to `CreateDefault()`.
- Config save failures: swallow the exception (the user's runtime state is still valid).
- Shortcut launch failures: swallow the exception.
- Icon load failures: use a shell32.dll fallback icon or render a placeholder rectangle.

### Rendering
- All rendering is done to the DIB bitmap buffer in `Renderer.cs`. Never draw directly to the screen DC.
- Use premultiplied alpha for all pixel operations. The formula for source-over compositing:
  ```
  outA = srcA + dstA * (1 - srcA)
  outC = srcC_premul + dstC_premul * (1 - srcA)
  ```
- Text rendering requires a post-processing step: GDI `TextOutW` writes RGB but leaves alpha at 0. `DrawAlphaText` detects new pixels and sets their alpha to the desired value.
- Keep rendering event-driven. `RequestRedraw()` posts `WM_USER+2` and sets a `_needsRedraw` flag. Multiple calls within one message batch result in a single render.
- **Never add a timer for rendering.** The only permitted timer use would be for animations, which are not currently implemented.

### State Management
- All persistent state lives in `AppConfig` (defined in `FenceData.cs`).
- `InputHandler` owns transient interaction state (drag state, hover, edit mode).
- `Renderer` owns GDI resources (DC, DIB, fonts) and has no persistent state.
- `TrayIcon` owns the notification icon lifecycle.
- Config is saved to disk on every meaningful user action (fence move, shortcut add/remove, etc.).

---

## Adding New Features — Checklist

When adding a new feature to DesktopFences:

1. **Does it require network access?** If yes, **stop. Do not implement it.**
2. **Does it require a new NuGet package?** If yes, verify it is:
   - Offline-only (no network calls, no telemetry)
   - AOT-compatible (no reflection, no `dynamic`)
   - Worth the binary size increase
3. **Does it add new persisted data?** If yes:
   - Add properties to the appropriate model class in `FenceData.cs`
   - Add `[JsonPropertyName]` attributes
   - Register any new types in `AppJsonContext`
   - Handle missing fields gracefully on config load (backward compat)
4. **Does it need new Win32 APIs?** If yes:
   - Add `LibraryImport` declarations to `NativeMethods.cs`
   - Add any new structs/constants there too
   - Use `StringMarshalling.Utf16` for string parameters
5. **Does it need new UI?** If yes:
   - For simple input: extend `TextInputDialog` or create a similar pure Win32 dialog
   - For rendering changes: modify `Renderer.DrawFence()` or add new draw methods
   - For interaction changes: add handling in `InputHandler`
6. **Does it allocate memory in the render path?** Minimize allocations. Use `stackalloc` for small buffers. Avoid LINQ in hot paths.
7. **Does it create GDI objects?** Ensure they are cleaned up in `Dispose()` or immediately after use.

---

## Win32 API Reference

### DLLs Used
| DLL | Purpose |
|-----|---------|
| user32.dll | Windowing, input, menus, cursors, layered windows, tray icons, message loop |
| gdi32.dll | Device contexts, DIB sections, fonts, text, brushes, pens, drawing primitives |
| shell32.dll | `Shell_NotifyIconW` (tray), `SHGetFileInfoW` (icons), `ExtractIconW` (icons) |
| dwmapi.dll | `DwmExtendFrameIntoClientArea`, `DwmSetWindowAttribute`, blur-behind |
| kernel32.dll | `GetModuleHandleW` |
| comdlg32.dll | `GetOpenFileNameW` (open-file dialog) |
| msimg32.dll | `AlphaBlend` (declared but rendering uses manual compositing instead) |

### Key Patterns

**Always-on-bottom window:**
```csharp
// In WM_WINDOWPOSCHANGING:
var pos = (WINDOWPOS*)lParam;
pos->hwndInsertAfter = HWND_BOTTOM;
```

**Click-through for empty areas:**
```csharp
// In WM_NCHITTEST:
var hit = _input.HitTest(pt.X, pt.Y);
return hit.Fence == null
    ? new IntPtr(-1)   // HTTRANSPARENT — pass clicks through
    : new IntPtr(1);   // HTCLIENT — handle clicks
```

**Event-driven redraw:**
```csharp
void RequestRedraw() {
    if (!_needsRedraw) {
        _needsRedraw = true;
        PostMessageW(_hwnd, WM_USER + 2, IntPtr.Zero, IntPtr.Zero);
    }
}
```

---

## Testing

No automated test framework is included (test frameworks would add dependencies and binary size). Manual testing checklist:

- [ ] App starts and displays two default fences
- [ ] Right-click creates a new fence at cursor position
- [ ] Fence title inline editing (type, backspace, enter, escape)
- [ ] Drag fence by title bar to reposition
- [ ] Resize from all 8 edges/corners, respects min size
- [ ] Double-click title bar collapses/expands
- [ ] Add file shortcut shows Open File dialog, icon loads
- [ ] Add URL shortcut shows text input dialog
- [ ] Double-click shortcut launches it
- [ ] Drag shortcut from one fence to another
- [ ] Remove shortcut via context menu
- [ ] Delete fence removes it and cleans up icons
- [ ] Tray icon appears; right-click shows menu
- [ ] Edit Mode toggle disables drag/resize
- [ ] Start with Windows toggle writes/removes registry key
- [ ] Exit from tray saves config and closes cleanly
- [ ] Click on empty area passes through to desktop
- [ ] Window stays behind all other windows at all times
- [ ] Resolution change resizes the overlay correctly
- [ ] Second instance exits immediately (single-instance mutex)
- [ ] Config file is valid JSON after every operation
- [ ] Runtime memory stays under 20MB
