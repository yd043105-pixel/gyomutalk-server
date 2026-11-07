using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SchoolMessenger.Desktop;

// Initial-letter avatars and presence dots shared by every window.
static class Avatar
{
    static readonly (Color Back, Color Ink)[] Tints =
    [
        (Hex("#DCE8FF"), Hex("#1E4FC2")), (Hex("#D7F2EC"), Hex("#0F7A62")), (Hex("#FDE9CF"), Hex("#9A5B05")), (Hex("#F7DDE2"), Hex("#B0304A")),
        (Hex("#E9E1FB"), Hex("#5B3CB8")), (Hex("#E3EEF7"), Hex("#2C5F86")), (Hex("#E6F3D9"), Hex("#3F7417")), (Hex("#ECEFF3"), Hex("#47566B"))
    ];
    static Color Hex(string value) => (Color)ColorConverter.ConvertFromString(value);
    // Stable across runs (string.GetHashCode is randomized per process).
    static int Slot(string seed) { var sum = 0; foreach (var c in seed) sum = sum * 31 + c; return (int)((uint)sum % (uint)Tints.Length); }

    // For data templates: the same tint pair as Create.
    public static Brush Back(string seed) => SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Tints[Slot(seed)].Back);
    public static Brush Ink(string seed) => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Tints[Slot(seed)].Ink);
    public static string Initial(string name) => name.Trim().Length > 0 ? name.Trim()[..1] : "?";

    public static Border Create(string name, double size, string? seed = null)
    {
        var tint = Tints[Slot(string.IsNullOrWhiteSpace(seed) ? name : seed)];
        var high = SystemParameters.HighContrast;
        var letter = new TextBlock
        {
            Text = name.Trim().Length > 0 ? name.Trim()[..1] : "?", FontSize = Math.Round(size * .4), FontWeight = FontWeights.SemiBold,
            Foreground = high ? SystemColors.WindowTextBrush : new SolidColorBrush(tint.Ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = letter, VerticalAlignment = VerticalAlignment.Center,
            Background = high ? SystemColors.WindowBrush : new SolidColorBrush(tint.Back), BorderBrush = high ? SystemColors.WindowTextBrush : null, BorderThickness = new Thickness(high ? 1 : 0)
        };
    }

    public static Ellipse Dot(bool online, bool away, bool mobile = false)
    {
        var color = !online ? (mobile ? Hex("#3B82F6") : Colors.White) : away ? Hex("#F59E0B") : Hex("#16A34A");
        var ring = online || mobile ? Colors.White : Hex("#94A3B8");
        if (SystemParameters.HighContrast) { color = online ? SystemColors.WindowTextColor : SystemColors.WindowColor; ring = SystemColors.WindowTextColor; }
        return new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), Stroke = new SolidColorBrush(ring), StrokeThickness = online || mobile ? 2 : 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    }

    // Avatar with a presence dot in the lower right corner; the tooltip and accessible name carry the state.
    public static Grid WithPresence(string name, double size, string? seed, bool online, bool away, bool mobile, string label)
    {
        var mark = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, ToolTip = label };
        mark.Children.Add(Create(name, size, seed)); mark.Children.Add(Dot(online, away, mobile));
        ToolTipService.SetInitialShowDelay(mark, 500); System.Windows.Automation.AutomationProperties.SetName(mark, label);
        return mark;
    }
}
