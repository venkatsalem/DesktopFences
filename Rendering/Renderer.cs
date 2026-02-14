using System.Runtime.InteropServices;
using DesktopFences.Interop;
using DesktopFences.Models;
using static DesktopFences.Interop.NativeMethods;

namespace DesktopFences.Rendering;

/// <summary>
/// Renders all fences to a 32-bit ARGB DIB section and updates the layered window.
/// Uses raw GDI for maximum compatibility with AOT and minimal memory.
/// </summary>
internal sealed class Renderer : IDisposable
{
    private IntPtr _screenDC;
    private IntPtr _memDC;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private IntPtr _bitmapBits;
    private int _width;
    private int _height;

    // Fonts
    private IntPtr _titleFont;
    private IntPtr _itemFont;
    private IntPtr _smallFont;

    // Cached brushes/pens
    private bool _resourcesCreated;

    public void EnsureTarget(IntPtr hwnd, int width, int height)
    {
        if (width == _width && height == _height && _memDC != IntPtr.Zero)
            return;

        Cleanup();

        _width = width;
        _height = height;
        _screenDC = GetDC(IntPtr.Zero);
        _memDC = CreateCompatibleDC(_screenDC);

        // Create 32-bit ARGB DIB
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        _bitmap = CreateDIBSection(_screenDC, ref bmi, 0, out _bitmapBits, IntPtr.Zero, 0);
        _oldBitmap = SelectObject(_memDC, _bitmap);

        if (!_resourcesCreated)
        {
            _titleFont = CreateFontW(-16, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _itemFont = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _smallFont = CreateFontW(-11, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _resourcesCreated = true;
        }
    }

    public void Render(IntPtr hwnd, AppConfig config, HitTestInfo? hover, FenceData? editingFence, string? editText, int editCursorPos)
    {
        if (_memDC == IntPtr.Zero) return;

        // Clear to fully transparent
        ClearBitmap();

        foreach (var fence in config.Fences)
        {
            DrawFence(fence, config.EditMode, hover, editingFence, editText, editCursorPos);
        }

        // Update the layered window
        var ptSrc = new POINT { X = 0, Y = 0 };
        var ptDst = new POINT { X = 0, Y = 0 };
        var size = new SIZE { cx = _width, cy = _height };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        // Get window position for ptDst
        GetWindowRect(hwnd, out var wr);
        ptDst.X = wr.Left;
        ptDst.Y = wr.Top;

        UpdateLayeredWindow(hwnd, _screenDC, ref ptDst, ref size, _memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);
    }

    private void ClearBitmap()
    {
        if (_bitmapBits == IntPtr.Zero) return;
        unsafe
        {
            var totalBytes = _width * _height * 4;
            new Span<byte>((void*)_bitmapBits, totalBytes).Clear();
        }
    }

    private void DrawFence(FenceData fence, bool editMode, HitTestInfo? hover, FenceData? editingFence, string? editText, int editCursorPos)
    {
        int displayHeight = fence.DisplayHeight;
        int x = fence.X, y = fence.Y, w = fence.Width, h = displayHeight;
        const int radius = 12;

        bool isHovered = hover?.Fence == fence;
        bool isEditing = editingFence == fence;

        // Draw fence background with rounded corners and semi-transparency
        DrawRoundedRectFilled(x, y, w, h, radius, 0x40, 30, 30, 35); // dark semi-transparent

        // Draw border
        if (editMode && isHovered)
            DrawRoundedRectOutline(x, y, w, h, radius, 0xA0, 100, 140, 255); // blue highlight
        else
            DrawRoundedRectOutline(x, y, w, h, radius, 0x60, 80, 80, 90);

        // Title bar area
        DrawRoundedRectFilled(x + 1, y + 1, w - 2, FenceData.TitleBarHeight, radius, 0x50, 40, 40, 48);

        // Title text
        var oldFont = SelectObject(_memDC, _titleFont);
        SetBkMode(_memDC, 1); // TRANSPARENT

        string titleText;
        if (isEditing && editText != null)
        {
            titleText = editText + "|";
        }
        else
        {
            titleText = fence.Title;
        }

        // Collapse indicator
        string indicator = fence.Collapsed ? " [+]" : " [-]";
        var fullTitle = titleText + indicator;

        DrawAlphaText(_memDC, fullTitle, x + 12, y + 7, 0xFF, 220, 220, 230);
        SelectObject(_memDC, oldFont);

        // Title underline
        DrawHLine(x + 8, y + FenceData.TitleBarHeight, w - 16, 0x40, 100, 100, 110);

        // If collapsed, don't draw shortcuts
        if (fence.Collapsed) return;

        // Draw shortcuts
        int contentTop = y + FenceData.TitleBarHeight + 4;
        int contentLeft = x + FenceData.IconPadding;
        int cols = Math.Max(1, (w - FenceData.IconPadding * 2) / FenceData.IconCellSize);

        for (int i = 0; i < fence.Shortcuts.Count; i++)
        {
            var sc = fence.Shortcuts[i];
            int col = i % cols;
            int row = i / cols;
            int ix = contentLeft + col * FenceData.IconCellSize;
            int iy = contentTop + row * FenceData.IconCellSize;

            if (iy + FenceData.IconCellSize > y + h) break; // clip

            bool scHovered = isHovered && hover?.ShortcutIndex == i;

            // Highlight hovered shortcut
            if (scHovered)
            {
                DrawRoundedRectFilled(ix, iy, FenceData.IconCellSize, FenceData.IconCellSize, 6, 0x40, 80, 120, 200);
            }

            // Draw icon
            if (sc.CachedIcon != IntPtr.Zero)
            {
                DrawIconEx(_memDC, ix + FenceData.IconPadding, iy + 2,
                    sc.CachedIcon, FenceData.IconSize, FenceData.IconSize,
                    0, IntPtr.Zero, DI_NORMAL);
            }
            else
            {
                // Placeholder square
                DrawRoundedRectFilled(ix + FenceData.IconPadding + 4, iy + 6,
                    FenceData.IconSize - 8, FenceData.IconSize - 8, 4, 0x80, 100, 100, 130);
            }

            // Draw shortcut name below icon
            var nameFont = SelectObject(_memDC, _smallFont);
            string name = TruncateText(sc.Name, FenceData.IconCellSize - 4);
            GetTextExtentPoint32W(_memDC, name, name.Length, out var textSize);
            int textX = ix + (FenceData.IconCellSize - textSize.cx) / 2;
            DrawAlphaText(_memDC, name, textX, iy + FenceData.IconSize + 4, 0xD0, 200, 200, 210);
            SelectObject(_memDC, nameFont);
        }

        // Draw resize grip in bottom-right (if edit mode)
        if (editMode)
        {
            int gx = x + w - 14, gy = y + h - 14;
            for (int i = 0; i < 3; i++)
            {
                SetPixelAlpha(gx + i * 4, gy + 8, 0x80, 150, 150, 160);
                SetPixelAlpha(gx + i * 4 + 4, gy + 4, 0x80, 150, 150, 160);
                SetPixelAlpha(gx + 8, gy + i * 4, 0x80, 150, 150, 160);
            }
        }
    }

    /// <summary>
    /// Draw text with per-pixel alpha by rendering to a temp surface and compositing.
    /// GDI TextOut doesn't support alpha natively, so we pre-multiply.
    /// </summary>
    private void DrawAlphaText(IntPtr hdc, string text, int x, int y, byte alpha, byte r, byte g, byte b)
    {
        SetTextColor(hdc, RGB(r, g, b));
        SetBkMode(hdc, 1); // TRANSPARENT

        // Get text dimensions
        GetTextExtentPoint32W(hdc, text, text.Length, out var textSize);
        if (textSize.cx <= 0 || textSize.cy <= 0) return;

        // We need to render text to the DIB and fix up alpha
        // Save the region that will be affected
        int tw = textSize.cx + 2;
        int th = textSize.cy + 2;

        // Clamp to bitmap bounds
        if (x < 0 || y < 0 || x + tw > _width || y + th > _height) return;

        // Read current pixel values in the text area
        unsafe
        {
            byte* bits = (byte*)_bitmapBits;

            // Store old alpha values — use stackalloc for small regions, heap for large
            int totalPixels = tw * th;
            Span<byte> oldAlpha = totalPixels <= 4096
                ? stackalloc byte[totalPixels]
                : new byte[totalPixels];

            for (int py = 0; py < th && (y + py) < _height; py++)
            {
                for (int px = 0; px < tw && (x + px) < _width; px++)
                {
                    int offset = ((y + py) * _width + (x + px)) * 4;
                    oldAlpha[py * tw + px] = bits[offset + 3];
                }
            }

            // Draw the text
            TextOutW(hdc, x, y, text, text.Length);

            // Fix up alpha: where pixels changed, set alpha
            for (int py = 0; py < th && (y + py) < _height; py++)
            {
                for (int px = 0; px < tw && (x + px) < _width; px++)
                {
                    int offset = ((y + py) * _width + (x + px)) * 4;
                    byte oldA = oldAlpha[py * tw + px];

                    // If any channel changed, the text was drawn here
                    byte cb = bits[offset + 0];
                    byte cg = bits[offset + 1];
                    byte cr = bits[offset + 2];

                    // If the pixel has content (text was drawn)
                    if (cb != 0 || cg != 0 || cr != 0 || oldA != 0)
                    {
                        // Calculate text intensity based on what GDI drew
                        byte intensity = Math.Max(cr, Math.Max(cg, cb));

                        if (intensity > 0 && oldA == 0)
                        {
                            // New text pixel - premultiply alpha
                            float af = alpha / 255.0f;
                            bits[offset + 0] = (byte)(b * af); // B
                            bits[offset + 1] = (byte)(g * af); // G
                            bits[offset + 2] = (byte)(r * af); // R
                            bits[offset + 3] = alpha;           // A
                        }
                        // else: keep existing content
                    }
                }
            }
        }
    }

    private void DrawRoundedRectFilled(int x, int y, int w, int h, int radius, byte alpha, byte r, byte g, byte b)
    {
        if (w <= 0 || h <= 0) return;

        unsafe
        {
            byte* bits = (byte*)_bitmapBits;
            float premR = r * alpha / 255.0f;
            float premG = g * alpha / 255.0f;
            float premB = b * alpha / 255.0f;

            for (int py = 0; py < h; py++)
            {
                int absY = y + py;
                if (absY < 0 || absY >= _height) continue;

                for (int px = 0; px < w; px++)
                {
                    int absX = x + px;
                    if (absX < 0 || absX >= _width) continue;

                    // Check if this pixel is inside the rounded rect
                    if (!IsInsideRoundedRect(px, py, w, h, radius))
                        continue;

                    // Calculate edge antialiasing
                    float aa = GetRoundedRectAA(px, py, w, h, radius);
                    byte pixelAlpha = (byte)(alpha * aa);
                    float pR = premR * aa;
                    float pG = premG * aa;
                    float pB = premB * aa;

                    int offset = (absY * _width + absX) * 4;

                    // Alpha composite (source over)
                    byte dstA = bits[offset + 3];
                    if (dstA == 0)
                    {
                        bits[offset + 0] = (byte)pB;
                        bits[offset + 1] = (byte)pG;
                        bits[offset + 2] = (byte)pR;
                        bits[offset + 3] = pixelAlpha;
                    }
                    else
                    {
                        float srcAf = pixelAlpha / 255.0f;
                        float dstAf = dstA / 255.0f;
                        float outA = srcAf + dstAf * (1 - srcAf);
                        if (outA > 0)
                        {
                            bits[offset + 0] = (byte)((pB + bits[offset + 0] * (1 - srcAf)));
                            bits[offset + 1] = (byte)((pG + bits[offset + 1] * (1 - srcAf)));
                            bits[offset + 2] = (byte)((pR + bits[offset + 2] * (1 - srcAf)));
                            bits[offset + 3] = (byte)(outA * 255);
                        }
                    }
                }
            }
        }
    }

    private void DrawRoundedRectOutline(int x, int y, int w, int h, int radius, byte alpha, byte r, byte g, byte b)
    {
        if (w <= 0 || h <= 0) return;

        unsafe
        {
            byte* bits = (byte*)_bitmapBits;

            for (int py = 0; py < h; py++)
            {
                int absY = y + py;
                if (absY < 0 || absY >= _height) continue;

                for (int px = 0; px < w; px++)
                {
                    int absX = x + px;
                    if (absX < 0 || absX >= _width) continue;

                    // Only draw border pixels (1px wide)
                    bool isEdge = px == 0 || px == w - 1 || py == 0 || py == h - 1;
                    if (!isEdge) continue;

                    if (!IsInsideRoundedRect(px, py, w, h, radius))
                        continue;

                    float aa = GetRoundedRectAA(px, py, w, h, radius);
                    byte pixelAlpha = (byte)(alpha * aa);

                    int offset = (absY * _width + absX) * 4;
                    float srcAf = pixelAlpha / 255.0f;

                    bits[offset + 0] = (byte)(b * srcAf + bits[offset + 0] * (1 - srcAf));
                    bits[offset + 1] = (byte)(g * srcAf + bits[offset + 1] * (1 - srcAf));
                    bits[offset + 2] = (byte)(r * srcAf + bits[offset + 2] * (1 - srcAf));
                    bits[offset + 3] = (byte)Math.Min(255, bits[offset + 3] + pixelAlpha);
                }
            }
        }
    }

    private static bool IsInsideRoundedRect(int px, int py, int w, int h, int r)
    {
        // Check corners
        if (px < r && py < r)
            return DistSq(px, py, r, r) <= r * r;
        if (px >= w - r && py < r)
            return DistSq(px, py, w - r - 1, r) <= r * r;
        if (px < r && py >= h - r)
            return DistSq(px, py, r, h - r - 1) <= r * r;
        if (px >= w - r && py >= h - r)
            return DistSq(px, py, w - r - 1, h - r - 1) <= r * r;
        return true;
    }

    private static float GetRoundedRectAA(int px, int py, int w, int h, int r)
    {
        float dist = float.MaxValue;

        if (px < r && py < r)
            dist = MathF.Sqrt(DistSq(px, py, r, r)) - r;
        else if (px >= w - r && py < r)
            dist = MathF.Sqrt(DistSq(px, py, w - r - 1, r)) - r;
        else if (px < r && py >= h - r)
            dist = MathF.Sqrt(DistSq(px, py, r, h - r - 1)) - r;
        else if (px >= w - r && py >= h - r)
            dist = MathF.Sqrt(DistSq(px, py, w - r - 1, h - r - 1)) - r;
        else
            return 1.0f;

        if (dist < -1) return 1.0f;
        if (dist > 0) return 0.0f;
        return 1.0f + dist; // anti-alias the edge
    }

    private static int DistSq(int x1, int y1, int x2, int y2)
    {
        int dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private void DrawHLine(int x, int y, int w, byte alpha, byte r, byte g, byte b)
    {
        if (y < 0 || y >= _height) return;
        unsafe
        {
            byte* bits = (byte*)_bitmapBits;
            for (int px = 0; px < w; px++)
            {
                int absX = x + px;
                if (absX < 0 || absX >= _width) continue;
                int offset = (y * _width + absX) * 4;
                float af = alpha / 255.0f;
                bits[offset + 0] = (byte)(b * af + bits[offset + 0] * (1 - af));
                bits[offset + 1] = (byte)(g * af + bits[offset + 1] * (1 - af));
                bits[offset + 2] = (byte)(r * af + bits[offset + 2] * (1 - af));
                bits[offset + 3] = (byte)Math.Min(255, bits[offset + 3] + alpha);
            }
        }
    }

    private void SetPixelAlpha(int x, int y, byte alpha, byte r, byte g, byte b)
    {
        if (x < 0 || x >= _width || y < 0 || y >= _height) return;
        unsafe
        {
            byte* bits = (byte*)_bitmapBits;
            int offset = (y * _width + x) * 4;
            float af = alpha / 255.0f;
            bits[offset + 0] = (byte)(b * af);
            bits[offset + 1] = (byte)(g * af);
            bits[offset + 2] = (byte)(r * af);
            bits[offset + 3] = alpha;
        }
    }

    private string TruncateText(string text, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return "";
        GetTextExtentPoint32W(_memDC, text, text.Length, out var size);
        if (size.cx <= maxWidth) return text;

        for (int i = text.Length - 1; i > 0; i--)
        {
            var truncated = text[..i] + "..";
            GetTextExtentPoint32W(_memDC, truncated, truncated.Length, out size);
            if (size.cx <= maxWidth) return truncated;
        }
        return ".";
    }

    private void Cleanup()
    {
        if (_memDC != IntPtr.Zero && _oldBitmap != IntPtr.Zero)
            SelectObject(_memDC, _oldBitmap);
        if (_bitmap != IntPtr.Zero)
            DeleteObject(_bitmap);
        if (_memDC != IntPtr.Zero)
            DeleteDC(_memDC);
        if (_screenDC != IntPtr.Zero)
            ReleaseDC(IntPtr.Zero, _screenDC);

        _memDC = IntPtr.Zero;
        _bitmap = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
        _screenDC = IntPtr.Zero;
        _bitmapBits = IntPtr.Zero;
    }

    public void Dispose()
    {
        Cleanup();
        if (_titleFont != IntPtr.Zero) { DeleteObject(_titleFont); _titleFont = IntPtr.Zero; }
        if (_itemFont != IntPtr.Zero) { DeleteObject(_itemFont); _itemFont = IntPtr.Zero; }
        if (_smallFont != IntPtr.Zero) { DeleteObject(_smallFont); _smallFont = IntPtr.Zero; }
    }
}

/// <summary>
/// Describes what the mouse is hovering over.
/// </summary>
internal sealed class HitTestInfo
{
    public FenceData? Fence { get; set; }
    public HitZone Zone { get; set; }
    public int ShortcutIndex { get; set; } = -1;
}

internal enum HitZone
{
    None,
    TitleBar,
    Body,
    Shortcut,
    ResizeLeft,
    ResizeRight,
    ResizeTop,
    ResizeBottom,
    ResizeTopLeft,
    ResizeTopRight,
    ResizeBottomLeft,
    ResizeBottomRight
}
