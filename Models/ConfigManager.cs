using System.Text.Json;

namespace DesktopFences.Models;

/// <summary>
/// Handles loading/saving the config JSON in %APPDATA%\DesktopFences.
/// </summary>
internal static class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DesktopFences");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
                if (config != null)
                {
                    // Restore expanded heights
                    foreach (var fence in config.Fences)
                    {
                        fence.ExpandedHeight = fence.Height;
                    }
                    return config;
                }
            }
        }
        catch
        {
            // Corrupted config — start fresh
        }

        return CreateDefault();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);

            // Temporarily swap in expanded heights for collapsed fences so
            // the persisted JSON always stores the full (uncollapsed) height.
            var savedHeights = new Dictionary<FenceData, int>();
            foreach (var fence in config.Fences)
            {
                if (fence.Collapsed && fence.ExpandedHeight > fence.Height)
                {
                    savedHeights[fence] = fence.Height;
                    fence.Height = fence.ExpandedHeight;
                }
            }

            var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
            File.WriteAllText(ConfigPath, json);

            // Restore in-memory heights
            foreach (var (fence, h) in savedHeights)
            {
                fence.Height = h;
            }
        }
        catch
        {
            // Silent fail — avoid crashing
        }
    }

    private static AppConfig CreateDefault()
    {
        var config = new AppConfig
        {
            EditMode = true,
            Fences =
            [
                new FenceData
                {
                    Title = "Applications",
                    X = 50,
                    Y = 50,
                    Width = 320,
                    Height = 280
                },
                new FenceData
                {
                    Title = "Documents",
                    X = 400,
                    Y = 50,
                    Width = 320,
                    Height = 280
                }
            ]
        };

        return config;
    }
}
