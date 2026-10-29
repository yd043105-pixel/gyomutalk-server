using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    string NewDraftPath() => Path.Combine(draftDirectory, Guid.NewGuid().ToString("N") + ".dpapi");
    void UpdateDraftCount() => DraftListButton.Content = $"임시저장 목록 ({Directory.EnumerateFiles(draftDirectory, "*.dpapi").Count()})";
    void DeleteSavedDraft(string path)
    {
        ReadDraft(path); File.Delete(path);
        if (draftPath == path)
        {
            pending = null; TitleBox.Clear(); BodyBox.Clear(); files.Clear(); uploaded.Clear();
            foreach (var person in people) person.Selected = false;
            CollectFiles.IsChecked = ScheduleSend.IsChecked = false; draftPath = NewDraftPath(); Unfreeze(); RefreshRecipients();
        }
        UpdateDraftCount();
    }
    Draft ReadDraft(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != draftDirectory || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) || new FileInfo(path).Length > 2_000_000)
            throw new InvalidDataException("올바른 임시저장 파일이 아닙니다.");
        var draft = JsonSerializer.Deserialize<Draft>(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), Json);
        if (draft is null || draft.Server != server || draft.Body is null || draft.Title is null || draft.Body.Length > 20_000 || draft.Title.Length > 200 || draft.RecipientIds is null || draft.Files is null || draft.Uploaded is null || !MessageFormatRules.Valid(draft.Format, draft.Body) || draft.Pending is { } pending && (!MessageFormatRules.Valid(pending.Format, pending.Body) || pending.Body != draft.Body))
            throw new InvalidDataException("현재 학교 계정의 임시저장을 읽을 수 없습니다.");
        return draft;
    }
    void MigrateLegacyDraft()
    {
        foreach (var old in new[] { legacyDraftPath, Path.ChangeExtension(legacyDraftPath, ".json") })
        {
            if (!File.Exists(old)) continue;
            try
            {
                if (new FileInfo(old).Length > 2_000_000) throw new InvalidDataException();
                var bytes = File.ReadAllBytes(old);
                var draft = JsonSerializer.Deserialize<Draft>(old.EndsWith(".dpapi", StringComparison.Ordinal) ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes, Json);
                if (draft is null || draft.Server != server) continue;
                var target = NewDraftPath();
                File.WriteAllBytes(target, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(draft with { SavedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(old)).ToUnixTimeMilliseconds() }, Json), null, DataProtectionScope.CurrentUser));
                File.Delete(old); Feedback.Text = "기존 작성 내용을 임시저장 목록으로 옮겼습니다.";
            }
            catch (Exception error) when (error is IOException or JsonException or CryptographicException)
            { Feedback.Text = "기존 임시저장을 옮기지 못했습니다. 원본 파일은 보관됩니다."; }
        }
    }
    void LoadDraft(string path)
    {
        var draft = ReadDraft(path);
        pending = null; Unfreeze(); draftPath = path;
        TitleBox.Text = draft.Title; BodyBox.Load(draft.Body, draft.Format); pending = draft.Pending;
        foreach (var person in people) person.Selected = draft.RecipientIds.Contains(person.Id) || draft.All || draft.Department == person.Department;
        files.Clear(); uploaded.Clear();
        foreach (var file in draft.Files) if (File.Exists(file.Path)) files.Add(file);
        uploaded.AddRange(draft.Uploaded);
        CollectFiles.IsChecked = draft.CollectRequested || draft.SubmissionDeadline is not null;
        if (draft.CollectionDate is not null) SubmissionDate.SelectedDate = ParseDay(draft.CollectionDate);
        else if (draft.SubmissionDeadline is { } deadline) SubmissionDate.SelectedDate = DateTimeOffset.FromUnixTimeMilliseconds(deadline).ToOffset(TimeSpan.FromHours(9)).Date;
        ScheduleSend.IsChecked = draft.ReserveRequested || draft.ScheduledAt is not null;
        if (draft.ReserveRequested) { ScheduleDate.SelectedDate = ParseDay(draft.ReserveDate); ScheduleTime.Text = draft.ReserveTime ?? ""; }
        else if (draft.ScheduledAt is { } at) { var time = DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(TimeSpan.FromHours(9)); ScheduleDate.SelectedDate = time.Date; ScheduleTime.Text = time.ToString("HH:mm"); }
        Department.SelectedIndex = 0; SendAll.IsChecked = false; RefreshRecipients();
        Feedback.Text = files.Count == draft.Files.Length ? "임시저장을 불러왔습니다." : "일부 첨부파일을 찾지 못했습니다. 해당 파일을 다시 첨부하세요.";
        if (pending is not null) { Freeze(); Feedback.Text = "전송 결과 미확인. 같은 메시지를 재시도합니다. 내용 변경은 완료 확인 후 가능합니다."; }
        static DateTime? ParseDay(string? day) => DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }
    void ShowDrafts(object sender, RoutedEventArgs e)
    {
        if (sending || pending is not null) return;
        try
        {
            var window = new Window { Owner = this, Title = "임시저장 목록", Width = 570, Height = 470, MinWidth = 480, MinHeight = 350, Resources = Resources, Style = (Style)FindResource("SchoolWindow"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var dock = new DockPanel { Margin = new Thickness(18) }; window.Content = dock;
            var note = new TextBlock { Text = "이 PC에 저장한 메시지입니다. 선택한 내용을 이어서 작성하세요.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) }; DockPanel.SetDock(note, Dock.Top); dock.Children.Add(note);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
            var remove = new Button { Content = "선택 삭제", Margin = new Thickness(0, 0, 8, 0) }; var open = new Button { Content = "불러오기", Style = (Style)FindResource("PrimaryButton") }; buttons.Children.Add(remove); buttons.Children.Add(open);
            var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding("Label"));
            label.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); label.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
            var list = new ListBox { Name = "SavedDrafts", ItemTemplate = new DataTemplate { VisualTree = label } }; list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled); dock.Children.Add(list);
            void Refresh()
            {
                var rows = new List<SavedDraftRow>(); var failed = 0;
                foreach (var path in Directory.EnumerateFiles(draftDirectory, "*.dpapi").OrderByDescending(File.GetLastWriteTimeUtc).Take(1000))
                {
                    try
                    {
                        var draft = ReadDraft(path); var title = SchoolMessenger.Shared.MessageText.Excerpt(string.IsNullOrWhiteSpace(draft.Title) ? draft.Body : draft.Title, 55);
                        var date = DateTimeOffset.FromUnixTimeMilliseconds(draft.SavedAt).ToOffset(TimeSpan.FromHours(9)).ToString("MM.dd HH:mm");
                        rows.Add(new SavedDraftRow(path, draft, title + "\n" + date + $" · 수신 {draft.RecipientIds.Length}명 · 첨부 {draft.Files.Length}개" + (draft.Pending is null ? "" : " · 전송 확인 필요")));
                    }
                    catch (Exception error) when (error is IOException or JsonException or CryptographicException or ArgumentException) { failed++; }
                }
                list.ItemsSource = rows; open.IsEnabled = remove.IsEnabled = false;
                note.Text = rows.Count == 0 ? "임시저장한 메시지가 없습니다. 작성 창에서 임시저장을 눌러 주세요." : "선택한 메시지를 이어서 작성하세요. 현재 작성 중인 내용도 따로 보관됩니다.";
                if (failed > 0) note.Text += $"\n읽지 못한 파일 {failed}개는 삭제하지 않고 보관했습니다.";
                UpdateDraftCount();
            }
            void Open()
            {
                if (list.SelectedItem is not SavedDraftRow row) return;
                try { SaveDraft(); LoadDraft(row.Path); window.Close(); } catch (Exception error) { note.Text = error.Message; }
            }
            open.Click += (_, _) => Open(); list.MouseDoubleClick += (_, _) => Open();
            list.SelectionChanged += (_, _) => open.IsEnabled = remove.IsEnabled = list.SelectedItem is SavedDraftRow;
            remove.Click += (_, _) =>
            {
                if (list.SelectedItem is not SavedDraftRow row) return;
                var warning = row.Value.Pending is null ? "선택한 임시저장을 삭제할까요?" : "서버에 이미 전송됐을 수 있습니다. 보낸 메시지를 확인한 뒤 임시저장을 삭제하세요. 삭제할까요?";
                if (MessageBox.Show(window, warning, "임시저장 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                try { DeleteSavedDraft(row.Path); Refresh(); } catch (Exception error) { note.Text = error.Message; }
            };
            Refresh(); window.ShowDialog();
        }
        catch (Exception error) { Feedback.Text = error.Message; }
    }
    sealed record SavedDraftRow(string Path, Draft Value, string Label);
}
