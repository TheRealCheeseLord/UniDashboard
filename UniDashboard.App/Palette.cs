using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace UniDashboard.App;

/// <summary>The app's colour language: one colour per kind of thing, used for icon chips, tags and calendar blocks.</summary>
internal static class Palette
{
    public static readonly Windows.UI.Color Course = Windows.UI.Color.FromArgb(255, 86, 160, 240);       // courses, lectures, sessions
    public static readonly Windows.UI.Color Exam = Windows.UI.Color.FromArgb(255, 238, 108, 96);         // exams
    public static readonly Windows.UI.Color Section = Windows.UI.Color.FromArgb(255, 232, 168, 56);      // curriculum sections
    public static readonly Windows.UI.Color Assignment = Windows.UI.Color.FromArgb(255, 72, 200, 172);   // assignments
    public static readonly Windows.UI.Color Curriculum = Windows.UI.Color.FromArgb(255, 160, 140, 240);  // the study itself, registration
    public static readonly Windows.UI.Color Done = Windows.UI.Color.FromArgb(255, 108, 203, 95);         // enrolled, submitted, registered
    public static readonly Windows.UI.Color Warning = Windows.UI.Color.FromArgb(255, 252, 185, 60);      // soon due, waitlist
    public static readonly Windows.UI.Color Muted = Windows.UI.Color.FromArgb(255, 160, 160, 160);

    public static SolidColorBrush Solid(Windows.UI.Color c, byte alpha = 255) => new(Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
    public static SolidColorBrush Tint(Windows.UI.Color c, byte alpha = 0x30) => Solid(c, alpha);
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A rounded icon chip tinted with the kind colour.</summary>
    public static Microsoft.UI.Xaml.Controls.Border Chip(string glyph, Windows.UI.Color color, double size = 34, double icon = 17) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 4.2), VerticalAlignment = VerticalAlignment.Center, Background = Tint(color),
        Child = new Microsoft.UI.Xaml.Controls.FontIcon { Glyph = glyph, FontSize = icon, Foreground = Solid(color) },
    };
}
