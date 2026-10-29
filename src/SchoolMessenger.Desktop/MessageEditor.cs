using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed class MessageEditor : RichTextBox
{
    public int MaxLength { get; set; } = 20_000;
    public MessageEditor()
    {
        FontFamily = new FontFamily("Malgun Gothic"); FontSize = 16;
        Document = NewDocument(); AcceptsTab = true; IsDocumentEnabled = false;
        DataObject.AddPastingHandler(this, (_, e) =>
        {
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) { e.CancelCommand(); return; }
            var text = (string)e.DataObject.GetData(DataFormats.UnicodeText);
            var available = Math.Max(0, MaxLength - Text.Length + Selection.Text.Length);
            if (text.Length > available) text = text[..available];
            e.DataObject = new DataObject(DataFormats.UnicodeText, text);
        });
        PreviewTextInput += (_, e) => { if (Text.Length - Selection.Text.Length + e.Text.Length > MaxLength) e.Handled = true; };
        PreviewDrop += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Handled = true;
            if (e.Data.GetData(DataFormats.UnicodeText) is string text)
            {
                var available = Math.Max(0, MaxLength - Text.Length + Selection.Text.Length);
                Selection.Text = text.Length <= available ? text : text[..available];
            }
        };
    }
    FlowDocument NewDocument() => new() { PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = FontSize };
    public string Text { get => string.Concat(Formatting.Runs.Select(r => r.Text)); set => Load(value, null); }
    public void Clear() => Text = "";
    public MessageFormat Formatting
    {
        get
        {
            var rows = new List<MessageRun>(); var first = true;
            foreach (var block in Document.Blocks)
            {
                if (!first) rows.Add(new MessageRun("\n")); first = false;
                if (block is Paragraph paragraph) Add(paragraph.Inlines);
                else rows.Add(new MessageRun(new TextRange(block.ContentStart, block.ContentEnd).Text));
            }
            return new MessageFormat(rows.Count == 0 ? [new MessageRun("")] : rows.ToArray());
            void Add(InlineCollection inlines)
            {
                foreach (var inline in inlines)
                {
                    if (inline is Span span) { Add(span.Inlines); continue; }
                    var text = inline is Run run ? run.Text : inline is LineBreak ? "\n" : "";
                    if (text.Length == 0) continue;
                    var underline = false; var strike = false;
                    for (DependencyObject? parent = inline; parent is Inline ancestor; parent = ancestor.Parent)
                    {
                        underline |= ancestor.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
                        strike |= ancestor.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
                    }
                    string? Color(Brush brush) => brush is SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : null;
                    rows.Add(new MessageRun(text, inline.FontFamily.Source, inline.FontSize * 72 / 96, inline.FontWeight.ToOpenTypeWeight() >= 600,
                        inline.FontStyle != FontStyles.Normal, underline, strike, Color(inline.Foreground), inline.Background is SolidColorBrush background && background.Color.A > 0 ? Color(background) : null));
                }
            }
        }
    }
    public void Load(string text, MessageFormat? format)
    {
        var document = NewDocument(); var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (format is not null && MessageFormatRules.Valid(format, text)) foreach (var style in format.Runs) paragraph.Inlines.Add(MessageBody.MakeRun(style));
        else paragraph.Inlines.Add(new Run(text.Replace("\r\n", "\n")));
        document.Blocks.Add(paragraph); Document = document;
    }
}

public sealed class MessageBody : TextBlock
{
    public void SetFormattedText(string text, MessageFormat? format)
    {
        Inlines.Clear();
        if (format is not null && MessageFormatRules.Valid(format, text)) foreach (var style in format.Runs) Inlines.Add(MakeRun(style));
        else Text = text;
    }
    internal static Run MakeRun(MessageRun style)
    {
        var run = new Run(style.Text) { FontFamily = new FontFamily(style.Font), FontSize = style.Size * 96 / 72,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal };
        var decorations = new TextDecorationCollection();
        if (style.Underline) decorations.Add(System.Windows.TextDecorations.Underline[0]);
        if (style.Strike) decorations.Add(System.Windows.TextDecorations.Strikethrough[0]);
        run.TextDecorations = decorations;
        if (!SystemParameters.HighContrast)
        {
            if (style.Color is not null) run.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Color));
            if (style.Highlight is not null) run.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Highlight));
        }
        return run;
    }
}
