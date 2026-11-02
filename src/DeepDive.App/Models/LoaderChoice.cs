using DeepDive.Core.Launcher;

namespace DeepDive.App.Models;

/// <summary>
/// 加载器下拉里的一项（原版 + 三种模组加载器）。
/// </summary>
/// <remarks>
/// 直接把 <see cref="ModLoaderKind"/> 塞进 ComboBox 也能绑，但枚举的显示名会是
/// 「NeoForge」这种英文标识符，而且没法给「原版」加一句解释。包一层更省事。
/// </remarks>
public sealed class LoaderChoice
{
    public required ModLoaderKind Kind { get; init; }

    /// <summary>下拉里显示的文本。</summary>
    public required string Display { get; init; }

    /// <summary>选了具体加载器才需要挑版本号；原版不需要。</summary>
    public bool NeedsLoaderVersion => Kind != ModLoaderKind.Vanilla;

    /// <summary>四个固定选项，顺序就是下拉里的顺序。</summary>
    public static LoaderChoice[] All { get; } =
    [
        new() { Kind = ModLoaderKind.Vanilla,  Display = "原版（不装加载器）" },
        new() { Kind = ModLoaderKind.Fabric,   Display = "Fabric" },
        new() { Kind = ModLoaderKind.NeoForge, Display = "NeoForge" },
        new() { Kind = ModLoaderKind.Forge,    Display = "Forge" },
    ];

    public override string ToString() => Display;
}
