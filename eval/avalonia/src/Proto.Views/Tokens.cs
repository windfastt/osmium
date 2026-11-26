using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Osmium.Desktop.Theming;

namespace Proto.Views;

/// <summary>
/// Turns the shared colour tokens into theme resources: one brush per token (key = token name, e.g. "Card"),
/// a light and a dark dictionary, switched by <see cref="ThemeVariant"/> (follows Windows, or set per window).
/// </summary>
public static class Tokens
{
    public static void Install(IResourceDictionary resources)
    {
        resources.ThemeDictionaries[ThemeVariant.Light] = Build(dark: false);
        resources.ThemeDictionaries[ThemeVariant.Dark] = Build(dark: true);
    }

    private static ResourceDictionary Build(bool dark)
    {
        var d = new ResourceDictionary();
        foreach (var t in ColorTokens.All)
        {
            var c = Color.FromUInt32(0xFF000000u | (uint)t.Value(dark));
            d[t.Token.ToString()] = new SolidColorBrush(c);
            d[t.Token + "Color"] = c;
        }
        // Site --shadow, both layers, alpha taken from the rgba() values.
        d["Shadow"] = BoxShadows.Parse(dark
            ? "0 1 2 0 #66000000, 0 10 32 0 #61000000"
            : "0 1 2 0 #0F14151B, 0 10 32 0 #1714151B");
        // Fluent's own keys, so stock controls (TextBox, ScrollBar, focus) follow the palette too.
        d["SystemAccentColor"] = Color.FromUInt32(0xFF000000u | (uint)ColorTokens.Get(ColorToken.Accent, dark));
        d["TextControlBackground"] = d["Card"];
        d["TextControlBackgroundPointerOver"] = d["Card"];
        d["TextControlBackgroundFocused"] = d["Card"];
        d["TextControlBorderBrush"] = d["Line"];
        d["TextControlBorderBrushPointerOver"] = d["LineStrong"];
        d["TextControlBorderBrushFocused"] = d["Focus"];
        d["TextControlForeground"] = d["Text"];
        d["TextControlForegroundPointerOver"] = d["Text"];
        d["TextControlForegroundFocused"] = d["Text"];
        // Menus (the notification-area menu, context menus): surface, line, hover fill and text from the tokens.
        d["MenuFlyoutPresenterBackground"] = d["Card"];
        d["MenuFlyoutPresenterBorderBrush"] = d["Line"];
        d["MenuFlyoutItemBackgroundPointerOver"] = d["Subtle"];
        d["MenuFlyoutItemBackgroundPressed"] = d["Sunk"];
        d["MenuFlyoutItemForeground"] = d["Text"];
        d["MenuFlyoutItemForegroundPointerOver"] = d["Text"];
        d["MenuFlyoutItemForegroundPressed"] = d["Text"];
        d["MenuFlyoutItemForegroundDisabled"] = d["Muted"];
        d["MenuFlyoutSeparatorBackground"] = d["Line"];
        // DataGrid and TreeView selection: the accent at about 14 % on the card (REVIEW.md's rule).
        var sel = new SolidColorBrush(Color.FromArgb(0x24, (byte)(ColorTokens.Get(ColorToken.Accent, dark) >> 16),
            (byte)(ColorTokens.Get(ColorToken.Accent, dark) >> 8), (byte)ColorTokens.Get(ColorToken.Accent, dark)));
        d["DataGridRowSelectedBackgroundBrush"] = sel;
        d["DataGridRowSelectedUnfocusedBackgroundBrush"] = sel;
        d["DataGridRowSelectedHoveredBackgroundBrush"] = sel;
        d["DataGridRowHoveredBackgroundColor"] = d["SubtleColor"];
        d["DataGridColumnHeaderBackgroundBrush"] = d["Card"];
        d["DataGridColumnHeaderForegroundBrush"] = d["TextSecondary"];
        d["DataGridGridLinesBrush"] = d["LineSoft"];
        d["TreeViewItemBackgroundSelected"] = sel;
        d["TreeViewItemBackgroundSelectedPointerOver"] = sel;
        d["TreeViewItemForeground"] = d["Text"];
        return d;
    }
}
