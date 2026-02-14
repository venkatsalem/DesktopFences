using DesktopFences.Core;
using DesktopFences.Models;

// ──────────────────────────────────────────────────────────────────────────────
// Desktop Fences — Lightweight Windows 11 desktop organizer
//
// Security guarantee: This application makes ZERO network connections.
// No telemetry, no update checks, no analytics, no NuGet packages with
// network dependencies. All operations are local Win32 API calls against
// the filesystem, registry (HKCU only), and GDI rendering subsystem.
// Firewall-safe by design — can be fully blocked without any loss of function.
//
// Data storage: Single JSON file in %APPDATA%\DesktopFences\config.json
// Registry: Only HKCU\Software\Microsoft\Windows\CurrentVersion\Run (optional)
// ──────────────────────────────────────────────────────────────────────────────

namespace DesktopFences;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        // Prevent multiple instances
        using var mutex = new Mutex(true, "DesktopFences_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // Already running
            return;
        }

        // Load or create config
        var config = ConfigManager.Load();

        // Create and run the overlay window
        using var overlay = new OverlayWindow(config);
        overlay.Create();
        overlay.RunMessageLoop();
    }
}
