using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

// The 과목 lines of one 선택군 in the 신청 editor: one course per line with its 교과(군) and 유형.
// Typing narrows a list of 2022 개정 courses under the box; a listed name picks both kinds; pasting several lines
// (from 한글·엑셀) makes one line each; Enter adds a line.
// The list is a separate popup that never takes focus and the box's text is never rewritten while typing,
// so a Korean syllable being put together (ㄱ → 가 → 각) is not broken into loose letters.
sealed class CourseRows : StackPanel
{
    readonly ListBox list = new() { Focusable = false, MaxHeight = 260, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly TextBlock none = new() { Text = "목록에 없는 과목입니다. 그대로 적고 교과(군)·유형을 고르세요.", Margin = new Thickness(10, 8, 10, 8), FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly Popup popup = new() { Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = true, Focusable = false };
    TextBox? owner; bool picking;

    public CourseRows()
    {
        none.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var stack = new StackPanel(); stack.Children.Add(list); stack.Children.Add(none);
        var frame = new Border { Child = stack, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Margin = new Thickness(0, 2, 0, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Surface"); frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        popup.Child = frame;
        list.ItemContainerStyle = new Style(typeof(ListBoxItem), Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style)
        { Setters = { new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)), new Setter(MarginProperty, new Thickness(0)), new Setter(FocusableProperty, false) } };
        // The list stays where it opened, so it closes when the page scrolls or the window moves under it.
        var watched = false;
        Loaded += (_, _) =>
        {
            if (watched) return; watched = true;
            if (Window.GetWindow(this) is { } window) window.LocationChanged += (_, _) => Hide();
            for (DependencyObject? d = this; d is not null; d = VisualTreeHelper.GetParent(d))
                if (d is ScrollViewer page) { page.ScrollChanged += (_, e) => { if (e.VerticalChange != 0 || e.HorizontalChange != 0) Hide(); }; break; }
        };
        // A click picks the course without moving the focus out of the box.
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d is not null && d != list; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ListBoxItem { Content: CourseCatalog.Course c }) { Pick(c); e.Handled = true; return; }
        };
    }

    public static Grid Columns() { var grid = new Grid(); foreach (var w in new[] { 0, 8, 150, 8, 104, 6, 34 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) }); return grid; }

    public (string Name, string[] Kind)[] Items => Children.OfType<Grid>().Select(r => ((TextBox Course, ComboBox Area, ComboBox Type))r.Tag)
        .Select(r => (Name: Official(r.Course.Text), Kind: new[] { r.Area.SelectedItem as string ?? "", r.Type.SelectedItem as string ?? "" })).Where(r => r.Name.Length > 0).ToArray();

    public bool ListOpen => popup.IsOpen;
    public CourseCatalog.Course[] Listed => list.Items.Cast<CourseCatalog.Course>().ToArray();

    static string Official(string text) => CourseCatalog.Find(text)?.Name ?? text.Trim();

    // ▾ shows every course; typing shows the ones that match.
    void Show(TextBox box, bool all = false)
    {
        owner = box;
        var found = all ? CourseCatalog.All : CourseCatalog.Search(box.Text);
        list.ItemsSource = found; list.SelectedIndex = -1; list.Visibility = found.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        none.Visibility = found.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        // Opened once and left open while typing: closing and reopening the popup on every letter is what can upset the IME.
        if (!popup.IsOpen) { popup.PlacementTarget = box; popup.MinWidth = popup.MaxWidth = box.ActualWidth; popup.IsOpen = true; }
    }

    void Hide() { popup.IsOpen = false; owner = null; }

    void Pick(CourseCatalog.Course course)
    {
        if (owner is not { } box) return;
        Hide(); picking = true; box.Text = course.Name; picking = false; box.CaretIndex = box.Text.Length;
    }

    public TextBox Add(string text, string[]? kind = null, int at = -1, bool focus = false)
    {
        var line = Columns(); line.Margin = new Thickness(0, 0, 0, 6);
        var course = new TextBox { Text = text, MaxLength = 40, Padding = new Thickness(8, 6, 30, 6) };
        var arrow = new Button { Focusable = false, IsTabStop = false, Width = 28, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "과목 목록 보기" };
        arrow.Template = new ControlTemplate(typeof(Button)) { VisualTree = Arrow() };
        var cell = new Grid(); cell.Children.Add(course); cell.Children.Add(arrow);
        var area = new ComboBox { ItemsSource = CourseCatalog.Areas }; var type = new ComboBox { ItemsSource = CourseCatalog.Types };
        var remove = new Button { Content = "✕", Padding = new Thickness(0), ToolTip = "이 과목 빼기" };
        AutomationProperties.SetName(course, "과목 이름"); AutomationProperties.SetName(arrow, "과목 목록 보기"); AutomationProperties.SetName(area, "교과(군)"); AutomationProperties.SetName(type, "유형"); AutomationProperties.SetName(remove, "과목 빼기");
        Grid.SetColumn(area, 2); Grid.SetColumn(type, 4); Grid.SetColumn(remove, 6);
        line.Children.Add(cell); line.Children.Add(area); line.Children.Add(type); line.Children.Add(remove); line.Tag = (course, area, type);
        // A kind filled in from the list is cleared again when the name stops matching; one the teacher chose is left alone.
        var found = CourseCatalog.Find(text); var auto = kind is null && found is not null;
        area.SelectedItem = kind is null ? found?.Area : kind[0]; type.SelectedItem = kind is null ? found?.Type : kind[1];
        course.TextChanged += (_, _) =>
        {
            if (course.IsKeyboardFocusWithin && !picking) Show(course);
            if (CourseCatalog.Find(course.Text) is { } hit) { area.SelectedItem = hit.Area; type.SelectedItem = hit.Type; auto = true; }
            else if (auto) { area.SelectedIndex = -1; type.SelectedIndex = -1; auto = false; }
        };
        // "미적분 1" becomes "미적분Ⅰ" once the teacher leaves the box, so every school's results use the same names.
        course.LostKeyboardFocus += (_, _) => { if (owner == course) Hide(); if (Official(course.Text) != course.Text) course.Text = Official(course.Text); };
        arrow.Click += (_, _) => { if (popup.IsOpen && owner == course) Hide(); else { course.Focus(); Show(course, all: true); } };
        area.DropDownClosed += (_, _) => auto = false; type.DropDownClosed += (_, _) => auto = false;
        DataObject.AddPastingHandler(course, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
            var parts = pasted.Split(['\r', '\n', '\t', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            e.CancelCommand(); course.Text = parts[0];
            var next = Children.IndexOf(line) + 1; foreach (var p in parts.Skip(1)) Add(p, null, next++);
        });
        // While a Korean letter is being put together the keys arrive as ImeProcessed and are left to the IME.
        course.PreviewKeyDown += (_, e) =>
        {
            var open = popup.IsOpen && owner == course && list.Items.Count > 0;
            if (e.Key is Key.Down or Key.Up && open) { list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, list.Items.Count - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Escape && popup.IsOpen) { Hide(); e.Handled = true; }
            else if (e.Key == Key.Enter && open && list.SelectedItem is CourseCatalog.Course c) { Pick(c); e.Handled = true; }
            else if (e.Key == Key.Enter) { e.Handled = true; Hide(); Add("", null, Children.IndexOf(line) + 1, true); }
        };
        remove.Click += (_, _) => { if (owner == course) Hide(); Children.Remove(line); if (Children.Count == 0) Add(""); };
        Children.Insert(at < 0 ? Children.Count : at, line);
        if (focus) Dispatcher.InvokeAsync(() => course.Focus(), DispatcherPriority.Input);
        return course;
    }

    static FrameworkElementFactory Arrow()
    {
        var hit = new FrameworkElementFactory(typeof(Border)); hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M0,0 L4,4 L8,0"));
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Muted");
        path.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        path.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center); path.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        hit.AppendChild(path); return hit;
    }
}
