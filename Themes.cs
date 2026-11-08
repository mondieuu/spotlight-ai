using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Odak;

/// <summary>The same themes as the Mondium browser. System, Light and Dark have no colors of their own.</summary>
public sealed record AppTheme(string Key, string En, string Tr, string Mode, string? Frame = null, string? Toolbar = null, string? Omni = null, string? Accent = null);

public static class Themes
{
    public static readonly AppTheme[] All =
    {
        new("sistem", "System", "Sistem", "system"),
        new("acik", "Light", "Açık", "light"),
        new("koyu", "Dark", "Koyu", "dark"),
        new("mavi", "Blue", "Mavi", "light", "#c4d7f5", "#eef3fc", "#ffffff", "#0b57d0"),
        new("yesil", "Green", "Yeşil", "light", "#c3e3cc", "#eef8f1", "#ffffff", "#146c2e"),
        new("mor", "Purple", "Mor", "light", "#ddd0f4", "#f6f1fd", "#ffffff", "#6f43c0"),
        new("turuncu", "Orange", "Turuncu", "light", "#f6d6bd", "#fdf3ea", "#ffffff", "#b3541e"),
        new("pembe", "Pink", "Pembe", "light", "#f3cfdd", "#fdf1f6", "#ffffff", "#b0306a"),
        new("gece", "Midnight", "Gece mavisi", "dark", "#121a28", "#212d44", "#121a28", "#a8c7fa"),
        new("orman", "Forest", "Orman", "dark", "#111d17", "#1e3027", "#111d17", "#8fd4a8"),
        new("murdum", "Plum", "Mürdüm", "dark", "#1d1424", "#30223b", "#1d1424", "#d7b8f3"),
        new("kum", "Sand", "Kum", "light", "#e8dcc4", "#f7f1e3", "#ffffff", "#8a6a1f"),
        new("nane", "Mint", "Nane", "light", "#bfe6dd", "#ecf8f5", "#ffffff", "#0b7a6b"),
        new("gok", "Sky", "Gökyüzü", "light", "#bde3f5", "#ebf7fd", "#ffffff", "#0a6fa8"),
        new("limon", "Lemon", "Limon", "light", "#efe59a", "#fbf8dc", "#ffffff", "#7a6c00"),
        new("gul", "Rose", "Gül", "light", "#f5c6c6", "#fdeeee", "#ffffff", "#b3262e"),
        new("okyanus", "Ocean", "Okyanus", "dark", "#0b1f26", "#153440", "#0b1f26", "#7fd6e8"),
        new("civit", "Indigo", "Çivit", "dark", "#15143a", "#262560", "#15143a", "#b4b4ff"),
        new("bordo", "Burgundy", "Bordo", "dark", "#230f16", "#3a1c26", "#230f16", "#f2a7bd"),
        new("gunbat", "Sunset", "Gün batımı", "dark", "#26130d", "#3f2116", "#26130d", "#ffb087"),
        new("kahve", "Coffee", "Kahve", "dark", "#1f1712", "#33271f", "#1f1712", "#e3bd8f"),
        new("grafit", "Graphite", "Grafit", "dark", "#17191c", "#2a2d31", "#17191c", "#b9c4d0"),
    };

    public static AppTheme Find(string? key) => Array.Find(All, t => t.Key == key) ?? All[0];

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color AccentOf(AppTheme theme, bool dark) => theme.Accent != null ? Parse(theme.Accent) : Parse(dark ? "#a8c7fa" : "#0b57d0");

    /// <summary>Puts the theme's colors on the panel: the glass takes the tint of its frame color.</summary>
    public static void Apply(ResourceDictionary resources, AppTheme theme, bool dark, byte darkAlpha, byte lightAlpha)
    {
        var accent = AccentOf(theme, dark);
        resources["Accent"] = new SolidColorBrush(accent);
        // Pale accents on dark themes carry dark text, deep accents on light themes carry white text.
        resources["AccentFg"] = new SolidColorBrush(dark ? Color.FromRgb(0x10, 0x14, 0x1C) : Colors.White);
        if (theme.Frame == null) return;
        var frame = Parse(theme.Frame);
        resources["Tint"] = new SolidColorBrush(Color.FromArgb(dark ? darkAlpha : lightAlpha, frame.R, frame.G, frame.B));
    }
}
