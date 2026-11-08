using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    async Task EditRosterFolders()
    {
        const string path = "api/admin/roster-folders";
        RosterFolders snapshot;
        try { snapshot = await api.Get<RosterFolders>(path); }
        catch (ApiRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        { throw new InvalidOperationException("조직도 폴더를 관리하려면 학교 서버를 업데이트해 주세요."); }
        var users = (await api.Get<JsonElement>("api/admin/users")).EnumerateArray().Select(user =>
            (Id: Value(user, "id"), Label: Value(user, "name") + " · " + Value(user, "department") +
                (Flag(user, "active") ? "" : " · 중지"))).OrderBy(user => user.Label).ToArray();
        var folders = snapshot.Folders.ToDictionary(folder => folder.Id);
        string? selected = null;
        Page(false);
        var save = new Button { Content = "변경 저장", IsEnabled = false };
        save.SetResourceReference(StyleProperty, "PrimaryButton");
        Heading("조직도 폴더 관리", "학교 아래 두 단계까지 폴더를 만들 수 있습니다. 폴더를 선택한 뒤 들어갈 교직원을 체크하세요.", save,
            ("교직원 목록", async () =>
            {
                if (!save.IsEnabled || Notice.Show(InteractionOwner, "저장하지 않은 조직도 변경을 취소할까요?", "조직도",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await StaffList();
            }));
        var columns = new Grid { ColumnDefinitions =
            { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(16) }, new ColumnDefinition() } };
        body.Children.Add(columns);
        var left = new StackPanel();
        var right = new StackPanel();
        columns.Children.Add(AdminUi.Card(left, 16));
        var memberCard = AdminUi.Card(right, 16); Grid.SetColumn(memberCard, 2); columns.Children.Add(memberCard);
        left.Children.Add(AdminUi.Heading("폴더", 16));
        var tree = new TreeView { Height = 230, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(tree, "조직도 폴더");
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        var treeSurface = AdminUi.Card(tree, 6);
        treeSurface.Margin = new Thickness(0, 8, 0, 10);
        treeSurface.SetResourceReference(Border.BackgroundProperty, "Subtle");
        left.Children.Add(treeSurface);
        var name = new TextBox { MaxLength = 40, Margin = new Thickness(0, 4, 0, 8) };
        AutomationProperties.SetName(name, "폴더 이름");
        left.Children.Add(AdminUi.Label("폴더 이름", "Muted")); left.Children.Add(name);
        var actions = new WrapPanel(); left.Children.Add(actions);
        Button FolderAction(string label)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6) };
            actions.Children.Add(button);
            return button;
        }
        var add = FolderAction("폴더 추가");
        var child = FolderAction("하위 폴더 추가");
        var rename = FolderAction("이름 변경");
        var delete = FolderAction("폴더 삭제");
        var caption = AdminUi.Heading("폴더의 교직원", 16);
        right.Children.Add(caption);
        var searchLabel = AdminUi.Label("이름 또는 부서로 검색", "Muted", 12);
        searchLabel.Margin = new Thickness(0, 8, 0, 4);
        right.Children.Add(searchLabel);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(search, "배치할 교직원 검색");
        right.Children.Add(search);
        var members = new StackPanel();
        right.Children.Add(new ScrollViewer { Content = members, Height = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var tip = AdminUi.Label("여러 폴더에 같은 교직원을 넣을 수 있습니다. 어느 폴더에도 속하지 않으면 미분류에 표시됩니다.", "Muted", 12);
        tip.TextWrapping = TextWrapping.Wrap; right.Children.Add(tip);

        RosterFolder? Current() => selected is null ? null : folders.GetValueOrDefault(selected);
        void ChangeName(string? parent, string? id)
        {
            error.Text = "";
            var label = name.Text.Trim();
            if (label.Length == 0 || label.Any(char.IsControl) || (parent is null && label == "미분류"))
            { error.Text = "사용할 수 있는 폴더 이름을 입력하세요. ‘미분류’는 자동으로 표시됩니다."; name.Focus(); return; }
            if (folders.Values.Any(folder => folder.Id != id && folder.ParentId == parent &&
                folder.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
            { error.Text = "같은 위치에 이미 그 이름의 폴더가 있습니다."; return; }
            var folderId = id ?? Guid.NewGuid().ToString("N");
            folders[folderId] = new(folderId, parent, label, id is null ? [] : folders[id].MemberIds);
            save.IsEnabled = true; Render(folderId);
        }
        void Render(string? select)
        {
            tree.Items.Clear();
            var nodes = new Dictionary<string, TreeViewItem>();
            TreeViewItem Node(RosterFolder folder)
            {
                var node = new TreeViewItem { Header = folder.Name, Tag = folder.Id, IsExpanded = true };
                nodes.Add(folder.Id, node); return node;
            }
            foreach (var root in folders.Values.Where(folder => folder.ParentId is null).OrderBy(folder => folder.Name))
            {
                var item = Node(root); tree.Items.Add(item);
                foreach (var subfolder in folders.Values.Where(folder => folder.ParentId == root.Id).OrderBy(folder => folder.Name))
                    item.Items.Add(Node(subfolder));
            }
            if (select is not null && nodes.TryGetValue(select, out var row)) row.IsSelected = true;
            else { selected = null; Details(); }
        }
        void Details()
        {
            var folder = Current();
            name.Text = folder?.Name ?? "";
            child.IsEnabled = folder is { ParentId: null };
            rename.IsEnabled = delete.IsEnabled = search.IsEnabled = folder is not null;
            caption.Text = folder is null ? "폴더의 교직원" : folder.Name + " · 교직원";
            ShowMembers();
        }
        void ShowMembers()
        {
            members.Children.Clear();
            if (Current() is not { } folder)
            { members.Children.Add(AdminUi.Label("폴더를 만들거나 왼쪽에서 선택하세요.", "Muted")); return; }
            var assigned = folder.MemberIds.ToHashSet();
            foreach (var user in users.Where(user => user.Label.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var check = new CheckBox { Content = user.Label, IsChecked = assigned.Contains(user.Id), Margin = new Thickness(0, 4, 0, 4) };
                void Assign(object sender, RoutedEventArgs e)
                {
                    if (check.IsChecked == true) assigned.Add(user.Id); else assigned.Remove(user.Id);
                    folders[folder.Id] = folder with { MemberIds = assigned.ToArray() };
                    save.IsEnabled = true;
                }
                check.Checked += Assign; check.Unchecked += Assign;
                members.Children.Add(check);
            }
        }
        tree.SelectedItemChanged += (_, e) => { selected = (e.NewValue as TreeViewItem)?.Tag as string; Details(); };
        search.TextChanged += (_, _) => ShowMembers();
        add.Click += (_, _) => ChangeName(null, null);
        child.Click += (_, _) => ChangeName(selected, null);
        rename.Click += (_, _) => ChangeName(Current()?.ParentId, selected);
        delete.Click += (_, _) =>
        {
            if (Current() is not { } folder) return;
            if (Notice.Show(InteractionOwner, $"‘{folder.Name}’ 폴더와 그 아래 폴더를 삭제할까요? 교직원 계정은 그대로 유지됩니다.",
                "조직도 폴더 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var id in folders.Values.Where(item => item.ParentId == folder.Id).Select(item => item.Id).ToArray())
                folders.Remove(id);
            folders.Remove(folder.Id);
            error.Text = ""; save.IsEnabled = true; Render(folder.ParentId);
        };
        save.Click += async (_, _) => await Run(async () =>
        {
            snapshot = await api.Send<RosterFolders>(HttpMethod.Put, path, new RosterFolders(snapshot.Revision, folders.Values.ToArray()));
            save.IsEnabled = false; error.Text = "조직도 폴더를 저장했습니다.";
        });
        Render(folders.Values.FirstOrDefault(folder => folder.ParentId is null)?.Id);
    }
}
