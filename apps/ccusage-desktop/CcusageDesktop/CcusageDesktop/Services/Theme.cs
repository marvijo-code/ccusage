using Windows.UI;
using Microsoft.UI.Xaml.Media;

namespace CcusageDesktop.Services;

/// <summary>Fixed dark palette for the dashboard (code-built UI, no theme switching).</summary>
public static class Theme
{
    public static readonly Color BgColor = Color.FromArgb(0xFF, 0x0C, 0x10, 0x16);
    public static readonly Color CardColor = Color.FromArgb(0xFF, 0x14, 0x1A, 0x23);
    public static readonly Color CardAltColor = Color.FromArgb(0xFF, 0x1A, 0x21, 0x2C);
    public static readonly Color BorderColor = Color.FromArgb(0xFF, 0x25, 0x2E, 0x3A);
    public static readonly Color TextColor = Color.FromArgb(0xFF, 0xE6, 0xED, 0xF3);
    public static readonly Color MutedColor = Color.FromArgb(0xFF, 0x8B, 0x94, 0x9E);
    public static readonly Color FaintColor = Color.FromArgb(0xFF, 0x58, 0x63, 0x70);
    public static readonly Color AccentColor = Color.FromArgb(0xFF, 0x4C, 0xC2, 0xA9);
    public static readonly Color Accent2Color = Color.FromArgb(0xFF, 0x7C, 0x5C, 0xFF);
    public static readonly Color GoodColor = Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50);
    public static readonly Color WarnColor = Color.FromArgb(0xFF, 0xE3, 0xB3, 0x41);
    public static readonly Color HotColor = Color.FromArgb(0xFF, 0xF0, 0x6A, 0x6A);

    public static SolidColorBrush Bg => new(BgColor);
    public static SolidColorBrush Card => new(CardColor);
    public static SolidColorBrush CardAlt => new(CardAltColor);
    public static SolidColorBrush Stroke => new(BorderColor);
    public static SolidColorBrush Text => new(TextColor);
    public static SolidColorBrush Muted => new(MutedColor);
    public static SolidColorBrush Faint => new(FaintColor);
    public static SolidColorBrush Accent => new(AccentColor);
    public static SolidColorBrush Accent2 => new(Accent2Color);

    public const string MonoFont = "Cascadia Mono,Consolas";
    public const string UiFont = "Segoe UI Variable Display,Segoe UI";
}
