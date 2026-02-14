using System.Text.Json.Serialization;

namespace DesktopFences.Models;

/// <summary>
/// A single shortcut item inside a fence.
/// </summary>
public sealed class ShortcutItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// "file", "folder", or "url"
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "file";

    // Runtime-only (not serialized)
    [JsonIgnore]
    public IntPtr CachedIcon { get; set; }
}

/// <summary>
/// A fence region on the desktop.
/// </summary>
public sealed class FenceData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("title")]
    public string Title { get; set; } = "New Fence";

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; } = 380;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 340;

    [JsonPropertyName("collapsed")]
    public bool Collapsed { get; set; }

    [JsonPropertyName("shortcuts")]
    public List<ShortcutItem> Shortcuts { get; set; } = [];

    // Runtime-only
    [JsonIgnore]
    public int ExpandedHeight { get; set; } = 340;

    [JsonIgnore]
    public const int TitleBarHeight = 32;

    [JsonIgnore]
    public const int CollapsedHeight = TitleBarHeight + 4;

    [JsonIgnore]
    public const int IconSize = 48;

    [JsonIgnore]
    public const int IconPadding = 14;

    [JsonIgnore]
    public const int IconCellSize = IconSize + IconPadding * 2;

    /// <summary>Gets the actual display height considering collapsed state.</summary>
    [JsonIgnore]
    public int DisplayHeight => Collapsed ? CollapsedHeight : Height;
}

/// <summary>
/// Root config persisted to JSON.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("fences")]
    public List<FenceData> Fences { get; set; } = [];

    [JsonPropertyName("editMode")]
    public bool EditMode { get; set; } = true;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }
}

// Source generator for AOT-compatible JSON serialization
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(FenceData))]
[JsonSerializable(typeof(ShortcutItem))]
[JsonSerializable(typeof(List<FenceData>))]
[JsonSerializable(typeof(List<ShortcutItem>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class AppJsonContext : JsonSerializerContext;
