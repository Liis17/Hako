using System.Collections.Generic;

namespace Hako;

/// <summary>
/// Built-in build icons. Profiles store the SF Symbol names used by macOS (<c>iconSymbol</c>);
/// only the UI maps them to Segoe Fluent Icons glyphs.
/// </summary>
internal static class InstanceIconGlyphs
{
    public const string DefaultSymbol = "shippingbox.fill";

    public static IReadOnlyList<(string Symbol, string Glyph, string TitleKey)> BuiltIn { get; } =
    [
        ("shippingbox.fill", "", "IconBox"),
        ("cube.fill", "", "IconCube"),
        ("leaf.fill", "", "IconLeaf"),
        ("flame.fill", "", "IconFlame"),
        ("bolt.fill", "", "IconBolt"),
        ("moon.stars.fill", "", "IconMoon")
    ];

    /// <summary>Glyph for a stored symbol; empty (custom picture) and unknown symbols use the box.</summary>
    public static string Glyph(string? symbol)
    {
        foreach (var icon in BuiltIn)
        {
            if (string.Equals(icon.Symbol, symbol, StringComparison.Ordinal))
            {
                return icon.Glyph;
            }
        }

        return BuiltIn[0].Glyph;
    }
}
