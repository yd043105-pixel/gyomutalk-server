using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public sealed class RecipientWindow : Window
{
    readonly CheckBox[] choices;
    readonly TextBox name = new() { Margin = new Thickness(0, 6, 0, 16), MaxLength = 100 };
    public string[] SelectedIds => choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToArray();
    public string RoomName => name.Text.Trim();
    public RecipientWindow(MainWindow main, Person[] people, bool roomName, string[]? selected = null)
    {
        Owner = main; Style = (Style)main.FindResource("SchoolWindow"); Title = roomName ? "대화방 만들기" : "설문 대상 선택";
        Width = 440; Height = 580; MinWidth = 380; MinHeight = 450;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        if (roomName) { top.Children.Add(new TextBlock { Text = "방 이름 · 두 명 대화는 비워 두면 1:1 채팅", TextWrapping = TextWrapping.Wrap }); top.Children.Add(name); }
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10), ToolTip = "이름·부서 검색" }; top.Children.Add(search);
        var department = new ComboBox { ItemsSource = new[] { "부서 전체 선택" }.Concat(people.Select(p => p.Department).Distinct().Order()).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 12) }; top.Children.Add(department);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var cancel = new Button { Content = "취소", Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close(); bottom.Children.Add(cancel);
        var apply = new Button { Content = "선택 완료", Style = (Style)main.FindResource("PrimaryButton") }; apply.Click += (_, _) => { if (SelectedIds.Length == 0) { MessageBox.Show(this, "교직원을 선택하세요."); return; } DialogResult = true; }; bottom.Children.Add(apply);
        var list = new StackPanel(); root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        choices = people.OrderBy(p => p.Department).ThenBy(p => p.Name).Select(p => new CheckBox { Content = p.Label, Tag = p.Id, IsChecked = selected?.Contains(p.Id) == true, Margin = new Thickness(2, 7, 2, 7) }).ToArray();
        foreach (var choice in choices) list.Children.Add(choice);
        department.SelectionChanged += (_, _) => { if (department.SelectedIndex <= 0) return; var group = department.SelectedItem.ToString(); var ids = people.Where(p => p.Department == group).Select(p => p.Id).ToHashSet(); foreach (var choice in choices.Where(c => ids.Contains((string)c.Tag))) choice.IsChecked = true; department.SelectedIndex = 0; };
        search.TextChanged += (_, _) => { foreach (var choice in choices) choice.Visibility = choice.Content.ToString()!.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed; };
    }
}
