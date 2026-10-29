using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    bool updatingFormat;
    sealed record InkChoice(string Label, string? Value);
    void InitializeEditor()
    {
        updatingFormat = true;
        FontChoice.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).Where(MessageFormatRules.FontAllowed).Distinct().Order().ToArray();
        FontChoice.SelectedItem = "Malgun Gothic";
        SizeChoice.ItemsSource = new double[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72 }; SizeChoice.SelectedItem = 12d;
        ColorChoice.DisplayMemberPath = HighlightChoice.DisplayMemberPath = "Label";
        ColorChoice.ItemsSource = new[] { new InkChoice("글자 색", "#203048"), new InkChoice("빨강", "#BA3434"), new InkChoice("파랑", "#245BC6"), new InkChoice("초록", "#17745D"), new InkChoice("보라", "#7050B0") };
        HighlightChoice.ItemsSource = new[] { new InkChoice("강조 없음", null), new InkChoice("노랑", "#FFF09A"), new InkChoice("연두", "#D8F1C1"), new InkChoice("분홍", "#FFD9E3"), new InkChoice("하늘", "#D7EAFE") };
        ColorChoice.SelectedIndex = HighlightChoice.SelectedIndex = 0;
        updatingFormat = false;
        BodyBox.SelectionChanged += (_, _) =>
        {
            updatingFormat = true;
            try
            {
                if (BodyBox.Selection.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily font) FontChoice.SelectedItem = font.Source;
                if (BodyBox.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double size) SizeChoice.SelectedItem = Math.Round(size * 72 / 96, 1);
            }
            finally { updatingFormat = false; }
        };
    }
    void ApplyStyle(DependencyProperty property, object value)
    { if (updatingFormat || BodyBox is null || !BodyBox.IsEnabled) return; BodyBox.Selection.ApplyPropertyValue(property, value); BodyBox.Focus(); }
    void FontChanged(object sender, SelectionChangedEventArgs e)
    { if (FontChoice.SelectedItem is string font) ApplyStyle(TextElement.FontFamilyProperty, new FontFamily(font)); }
    void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    { if (SizeChoice.SelectedItem is double size) ApplyStyle(TextElement.FontSizeProperty, size * 96 / 72); }
    void ColorChanged(object sender, SelectionChangedEventArgs e)
    { if (ColorChoice.SelectedItem is InkChoice color && color.Value is not null) ApplyStyle(TextElement.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Value))); }
    void HighlightChanged(object sender, SelectionChangedEventArgs e)
    { if (HighlightChoice.SelectedItem is InkChoice color) ApplyStyle(TextElement.BackgroundProperty, color.Value is null ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Value))); }
    void BoldText(object sender, RoutedEventArgs e) { BodyBox.Focus(); EditingCommands.ToggleBold.Execute(null, BodyBox); }
    void ItalicText(object sender, RoutedEventArgs e) { BodyBox.Focus(); EditingCommands.ToggleItalic.Execute(null, BodyBox); }
    void UnderlineText(object sender, RoutedEventArgs e) { BodyBox.Focus(); EditingCommands.ToggleUnderline.Execute(null, BodyBox); }
    void StrikeText(object sender, RoutedEventArgs e)
    {
        var old = BodyBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var decorations = old?.Clone() ?? new TextDecorationCollection();
        if (decorations.Any(d => d.Location == TextDecorationLocation.Strikethrough))
            foreach (var item in decorations.Where(d => d.Location == TextDecorationLocation.Strikethrough).ToArray()) decorations.Remove(item);
        else decorations.Add(TextDecorations.Strikethrough[0]);
        ApplyStyle(Inline.TextDecorationsProperty, decorations);
    }
    void ClearFormatting(object sender, RoutedEventArgs e) { BodyBox.Selection.ClearAllProperties(); BodyBox.Focus(); }
}
