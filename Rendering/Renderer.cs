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
    private IntPtr _noteFont;

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
            _smallFont = CreateFontW(-13, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _noteFont = CreateFontW(-36, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
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

        // Fence body background — subtle ~8% opaque dark fill
        DrawRoundedRectFilled(x, y, w, h, radius, 0x14, 30, 30, 38);

        // Border — always visible
        if (editMode && isHovered)
            DrawRoundedRectOutline(x, y, w, h, radius, 0xC0, 100, 140, 255); // blue highlight
        else
            DrawRoundedRectOutline(x, y, w, h, radius, 0xB0, 120, 120, 135); // visible gray

        // Title bar background — same as original
        DrawRoundedRectFilled(x + 1, y + 1, w - 2, FenceData.TitleBarHeight, radius, 0x50, 40, 40, 48);

        // Title underline
        DrawHLine(x + 8, y + FenceData.TitleBarHeight, w - 16, 0x40, 100, 100, 110);

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

        DrawAlphaText(_memDC, titleText, x + 12, y + 7, 0xFF, 220, 220, 230);
        SelectObject(_memDC, oldFont);

        // If collapsed, don't draw shortcuts
        if (fence.Collapsed) return;

        // Draw shortcuts
        int contentTop = y + FenceData.TitleBarHeight + 4;
        int contentLeft = x + FenceData.IconPadding;
        int cols = Math.Max(1, (w - FenceData.IconPadding * 2) / FenceData.IconCellWidth);

        for (int i = 0; i < fence.Shortcuts.Count; i++)
        {
            var sc = fence.Shortcuts[i];
            int col = i % cols;
            int row = i / cols;
            int ix = contentLeft + col * FenceData.IconCellWidth;
            int iy = contentTop + row * FenceData.IconCellHeight;

            if (iy + FenceData.IconCellHeight > y + h) break; // clip

            bool scHovered = isHovered && hover?.ShortcutIndex == i;

            // Highlight hovered shortcut
            if (scHovered)
            {
                DrawRoundedRectFilled(ix, iy, FenceData.IconCellWidth, FenceData.IconCellHeight, 6, 0x40, 80, 120, 200);
            }

            // Draw icon centered in cell
            int iconX = ix + (FenceData.IconCellWidth - FenceData.IconSize) / 2;
            int iconY = iy + 4;

            if (sc.CachedIcon != IntPtr.Zero)
            {
                // Use manual pixel compositing — DrawIconEx corrupts alpha on premultiplied DIBs
                DrawIconManual(sc.CachedIcon, iconX, iconY, FenceData.IconSize, FenceData.IconSize);
            }
            else
            {
                // Placeholder square
                DrawRoundedRectFilled(iconX + 4, iconY + 4,
                    FenceData.IconSize - 8, FenceData.IconSize - 8, 4, 0x80, 100, 100, 130);
            }

            // Draw shortcut name below icon — up to 2 lines with word wrap
            var nameFont = SelectObject(_memDC, _smallFont);
            int maxTextW = FenceData.IconCellWidth - 4;
            int textTopY = iconY + FenceData.IconSize + 4;
            DrawWrappedName(_memDC, sc.Name, ix + 2, textTopY, maxTextW, FenceData.IconCellWidth);
            SelectObject(_memDC, nameFont);
        }

        // Draw note text below shortcuts (or in body if no shortcuts)
        if (!string.IsNullOrEmpty(fence.NoteText))
        {
            int noteTop;
            if (fence.Shortcuts.Count > 0)
            {
                int totalRows = (fence.Shortcuts.Count + cols - 1) / cols;
                noteTop = contentTop + totalRows * FenceData.IconCellHeight + 4;
            }
            else
            {
                noteTop = contentTop + 4;
            }

            var noteFont = SelectObject(_memDC, _noteFont);
            DrawNoteText(fence.NoteText, x + 12, noteTop, w - 24, y + h - noteTop - 8);
            SelectObject(_memDC, noteFont);
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
    /// Draws an icon by extracting its BGRA pixels via GetDIBits and compositing
    /// them manually onto the DIB. This avoids DrawIconEx alpha corruption issues
    /// on premultiplied-alpha surfaces.
    /// </summary>
    private void DrawIconManual(IntPtr hIcon, int destX, int destY, int drawW, int drawH)
    {
        if (!GetIconInfo(hIcon, out var iconInfo)) return;

        try
        {
            // Get bitmap info
            if (GetObjectW(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
                return;

            int iconW = bmp.bmWidth;
            int iconH = bmp.bmHeight;
            if (iconW <= 0 || iconH <= 0) return;

            // Set up BITMAPINFO for GetDIBits — request bottom-up 32bpp
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = iconW,
                    biHeight = -iconH, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            int pixelCount = iconW * iconH;
            var pixels = new byte[pixelCount * 4];

            unsafe
            {
                fixed (byte* pPixels = pixels)
                {
                    var hdc = CreateCompatibleDC(_screenDC);
                    int result = GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)iconH,
                        (IntPtr)pPixels, ref bmi, DIB_RGB_COLORS);
                    DeleteDC(hdc);

                    if (result == 0) return;
                }
            }

            // Check if the icon has any alpha (some old icons have all-zero alpha)
            bool hasAlpha = false;
            for (int i = 0; i < pixelCount; i++)
            {
                if (pixels[i * 4 + 3] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            // If no alpha channel, get the mask bitmap and use it
            byte[]? maskPixels = null;
            if (!hasAlpha && iconInfo.hbmMask != IntPtr.Zero)
            {
                var maskBmi = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                        biWidth = iconW,
                        biHeight = -iconH,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0
                    }
                };

                maskPixels = new byte[pixelCount * 4];
                unsafe
                {
                    fixed (byte* pMask = maskPixels)
                    {
                        var hdc = CreateCompatibleDC(_screenDC);
                        GetDIBits(hdc, iconInfo.hbmMask, 0, (uint)iconH,
                            (IntPtr)pMask, ref maskBmi, DIB_RGB_COLORS);
                        DeleteDC(hdc);
                    }
                }
            }

            // Composite onto the DIB
            unsafe
            {
                byte* dstBits = (byte*)_bitmapBits;

                for (int py = 0; py < iconH; py++)
                {
                    int absY = destY + py;
                    if (absY < 0 || absY >= _height) continue;

                    for (int px = 0; px < iconW; px++)
                    {
                        int absX = destX + px;
                        if (absX < 0 || absX >= _width) continue;

                        int srcOffset = (py * iconW + px) * 4;
                        byte sb = pixels[srcOffset + 0]; // B
                        byte sg = pixels[srcOffset + 1]; // G
                        byte sr = pixels[srcOffset + 2]; // R
                        byte sa = pixels[srcOffset + 3]; // A

                        if (!hasAlpha)
                        {
                            // Use mask to determine alpha
                            if (maskPixels != null)
                            {
                                byte maskVal = maskPixels[srcOffset]; // mask is white=transparent
                                sa = (byte)(maskVal > 0 ? 0 : 255);
                            }
                            else
                            {
                                sa = (sr > 0 || sg > 0 || sb > 0) ? (byte)255 : (byte)0;
                            }
                        }

                        if (sa == 0) continue; // fully transparent

                        int dstOffset = (absY * _width + absX) * 4;

                        // Premultiply source if it has alpha
                        float srcAf = sa / 255.0f;
                        byte premB = (byte)(sb * srcAf);
                        byte premG = (byte)(sg * srcAf);
                        byte premR = (byte)(sr * srcAf);

                        // Source-over composite
                        byte dstA = dstBits[dstOffset + 3];
                        if (dstA == 0)
                        {
                            dstBits[dstOffset + 0] = premB;
                            dstBits[dstOffset + 1] = premG;
                            dstBits[dstOffset + 2] = premR;
                            dstBits[dstOffset + 3] = sa;
                        }
                        else
                        {
                            float invSrcA = 1.0f - srcAf;
                            dstBits[dstOffset + 0] = (byte)(premB + dstBits[dstOffset + 0] * invSrcA);
                            dstBits[dstOffset + 1] = (byte)(premG + dstBits[dstOffset + 1] * invSrcA);
                            dstBits[dstOffset + 2] = (byte)(premR + dstBits[dstOffset + 2] * invSrcA);
                            float outA = srcAf + (dstA / 255.0f) * invSrcA;
                            dstBits[dstOffset + 3] = (byte)(outA * 255);
                        }
                    }
                }
            }
        }
        finally
        {
            // Clean up GDI objects from GetIconInfo
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
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

    /// <summary>
    /// Draws a shortcut name in up to 2 centered lines. If the text fits on one line, it is centered.
    /// If it needs two lines, it breaks at the last space that fits, or mid-word if no space.
    /// The second line is truncated with ".." if it still overflows.
    /// </summary>
    private void DrawWrappedName(IntPtr hdc, string name, int left, int topY, int maxWidth, int cellWidth)
    {
        if (string.IsNullOrEmpty(name)) return;

        GetTextExtentPoint32W(hdc, name, name.Length, out var fullSize);

        // Fits on one line
        if (fullSize.cx <= maxWidth)
        {
            int textX = left + (maxWidth - fullSize.cx) / 2;
            DrawAlphaText(hdc, name, textX, topY, 0xD0, 200, 200, 210);
            return;
        }

        // Find break point for first line — prefer breaking at a space
        int breakIdx = -1;
        for (int i = 1; i < name.Length; i++)
        {
            GetTextExtentPoint32W(hdc, name, i, out var partSize);
            if (partSize.cx > maxWidth)
            {
                breakIdx = i - 1;
                break;
            }
        }
        if (breakIdx <= 0) breakIdx = 1;

        // Try to break at last space before breakIdx
        int spaceIdx = name.LastIndexOf(' ', breakIdx);
        if (spaceIdx > 0)
            breakIdx = spaceIdx;

        string line1 = name[..breakIdx].TrimEnd();
        string line2 = name[breakIdx..].TrimStart();

        // Center line 1
        GetTextExtentPoint32W(hdc, line1, line1.Length, out var size1);
        int x1 = left + (maxWidth - size1.cx) / 2;
        DrawAlphaText(hdc, line1, x1, topY, 0xD0, 200, 200, 210);

        // Truncate line 2 if needed, then center it
        string line2Display = TruncateText(line2, maxWidth);
        GetTextExtentPoint32W(hdc, line2Display, line2Display.Length, out var size2);
        int lineHeight = size1.cy + 1;
        int x2 = left + (maxWidth - size2.cx) / 2;
        DrawAlphaText(hdc, line2Display, x2, topY + lineHeight, 0xD0, 200, 200, 210);
    }

    /// <summary>
    /// Renders multi-line note text with word wrapping within the given bounds.
    /// </summary>
    private void DrawNoteText(string text, int left, int top, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return;

        // Split by newlines first, then word-wrap each line
        var lines = text.Split('\n');
        int cursorY = top;

        // Measure line height
        GetTextExtentPoint32W(_memDC, "Ay", 2, out var measureSize);
        int lineHeight = measureSize.cy + 2;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrEmpty(line))
            {
                // Empty line — just advance
                cursorY += lineHeight;
                if (cursorY + lineHeight > top + maxHeight) break;
                continue;
            }

            // Word-wrap this line
            int start = 0;
            while (start < line.Length)
            {
                if (cursorY + lineHeight > top + maxHeight) return; // out of space

                // Find how many characters fit on this visual line
                int fitLen = 0;
                int lastSpace = -1;
                for (int i = start; i < line.Length; i++)
                {
                    int segLen = i - start + 1;
                    GetTextExtentPoint32W(_memDC, line.AsSpan(start, segLen).ToString(), segLen, out var segSize);
                    if (segSize.cx > maxWidth)
                        break;
                    fitLen = segLen;
                    if (line[i] == ' ')
                        lastSpace = segLen;
                }

                if (fitLen == 0) fitLen = 1; // at least one char

                int lineLen;
                int nextStart;
                if (start + fitLen >= line.Length)
                {
                    // Rest of the line fits
                    lineLen = line.Length - start;
                    nextStart = line.Length;
                }
                else if (lastSpace > 0)
                {
                    // Break at last space
                    lineLen = lastSpace;
                    nextStart = start + lastSpace; // skip past the space
                }
                else
                {
                    // No space — hard break
                    lineLen = fitLen;
                    nextStart = start + fitLen;
                }

                var segment = line.Substring(start, lineLen).TrimEnd();
                DrawAlphaText(_memDC, segment, left, cursorY, 0xE0, 210, 210, 220);

                cursorY += lineHeight;
                start = nextStart;
            }
        }
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
        if (_noteFont != IntPtr.Zero) { DeleteObject(_noteFont); _noteFont = IntPtr.Zero; }
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
