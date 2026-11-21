using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

// The 과목 lines of one 선택군 in the 신청 editor: one course per line with its 교과(군) and 유형.
// Typing a 2022 개정 course name picks both; pasting several lines (from 한글·엑셀) makes one line each; Enter adds a line.
sealed class CourseRows : StackPanel
{
    public static Grid Columns() { var grid = new Grid(); foreach (var w in new[] { 0, 8, 150, 8, 104, 6, 34 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) }); return grid; }

    public (string Name, string[] Kind)[] Items => Children.OfType<Grid>().Select(r => ((ComboBox Course, ComboBox Area, ComboBox Type))r.Tag)
        .Select(r => (Name: r.Course.Text.Trim(), Kind: new[] { r.Area.SelectedItem as string ?? "", r.Type.SelectedItem as string ?? "" })).Where(r => r.Name.Length > 0).ToArray();

    public ComboBox Add(string text, string[]? kind = null, int at = -1, bool focus = false)
    {
        var line = Columns(); line.Margin = new Thickness(0, 0, 0, 6);
        var course = new ComboBox { IsEditable = true, IsTextSearchEnabled = false, ItemsSource = CourseCatalog.All, MaxDropDownHeight = 320 };
        TextSearch.SetTextPath(course, "Name"); course.Text = text;
        var area = new ComboBox { ItemsSource = CourseCatalog.Areas }; var type = new ComboBox { ItemsSource = CourseCatalog.Types };
        var remove = new Button { Content = "✕", Padding = new Thickness(0), ToolTip = "이 과목 빼기" };
        AutomationProperties.SetName(course, "과목 이름"); AutomationProperties.SetName(area, "교과(군)"); AutomationProperties.SetName(type, "유형"); AutomationProperties.SetName(remove, "과목 빼기");
        Grid.SetColumn(area, 2); Grid.SetColumn(type, 4); Grid.SetColumn(remove, 6);
        line.Children.Add(course); line.Children.Add(area); line.Children.Add(type); line.Children.Add(remove); line.Tag = (course, area, type);
        // A kind filled in from the list is cleared again when the name stops matching; one the teacher chose is left alone.
        var found = CourseCatalog.Find(text); var auto = kind is null && found is not null;
        area.SelectedItem = kind is null ? found?.Area : kind[0]; type.SelectedItem = kind is null ? found?.Type : kind[1];
        var last = text;
        course.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (course.Text == last) return; last = course.Text;
            if (CourseCatalog.Find(course.Text) is { } hit) { area.SelectedItem = hit.Area; type.SelectedItem = hit.Type; auto = true; }
            else if (auto) { area.SelectedIndex = -1; type.SelectedIndex = -1; auto = false; }
        }));
        area.DropDownClosed += (_, _) => auto = false; type.DropDownClosed += (_, _) => auto = false;
        DataObject.AddPastingHandler(course, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
            var parts = pasted.Split(['\r', '\n', '\t', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;
            e.CancelCommand(); course.Text = parts[0];
            var next = Children.IndexOf(line) + 1; foreach (var p in parts.Skip(1)) Add(p, null, next++);
        });
        course.PreviewKeyDown += (_, e) => { if (e.Key != Key.Enter || course.IsDropDownOpen) return; e.Handled = true; Add("", null, Children.IndexOf(line) + 1, true); };
        remove.Click += (_, _) => { Children.Remove(line); if (Children.Count == 0) Add(""); };
        Children.Insert(at < 0 ? Children.Count : at, line);
        if (focus) Dispatcher.InvokeAsync(() => course.Focus(), DispatcherPriority.Input);
        return course;
    }
}
