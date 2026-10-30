using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow : Window
{
    readonly MainWindow main;
    readonly Person[] people;
    readonly ObservableCollection<SourceFile> files = new();
    readonly List<Attachment> uploaded = new();
    string draftPath;
    readonly string draftDirectory;
    readonly string server;
    readonly string legacyDraftPath;
    Pending? pending;
    bool sending, sent;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ComposeWindow(MainWindow main, Person[] people, string? recipient, string? title, string[]? selectedIds = null)
    {
        this.main = main; this.people = people;
        Owner = main; Resources = main.Resources;
        InitializeComponent(); StatusTone.Watch(Feedback);
        InitializeEditor();
        SubmissionDate.SelectedDate = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date.AddDays(7);
        ScheduleDate.SelectedDate = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).Date.AddDays(1);
        server = main.Api!.Address.ToString();
        legacyDraftPath = Path.Combine(main.LocalDirectory, $"draft-{main.Api.Session!.User!.Id}.dpapi");
        draftDirectory = Path.Combine(main.LocalDirectory, "drafts", Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(server + "|" + main.Api.Session.User.Id))));
        Directory.CreateDirectory(draftDirectory); draftPath = NewDraftPath();
        foreach (var person in people) { person.Selected = person.Id == recipient || selectedIds?.Contains(person.Id) == true; person.Favorite = main.Favorites.Contains(person.Id); }
        Department.ItemsSource = new[] { "선택 안 함" }.Concat(people.Select(p => p.Department).Distinct().Order()).ToArray();
        Department.SelectedIndex = 0;
        SendAll.Visibility = main.Api.Session.User.IsAdmin || main.Api.Session.User.CanBroadcast ? Visibility.Visible : Visibility.Collapsed;
        FilesList.ItemsSource = files; TitleBox.Text = title ?? "";
        MigrateLegacyDraft(); UpdateDraftCount();
        foreach(var person in people)if(SendAll.IsChecked==true||Department.SelectedIndex>0&&person.Department==(string)Department.SelectedItem)person.Selected=true;
        Department.SelectedIndex=0;SendAll.IsChecked=false;RefreshRecipients();
    }
    void FilterPeople(object sender, RoutedEventArgs e)
    {
        if (PeopleList is null || PeopleSearch is null || FavoritesOnly is null) return;
        var text = PeopleSearch.Text.Trim();
        PeopleList.ItemsSource = people.Where(p => p.Label.Contains(text, StringComparison.OrdinalIgnoreCase) && (FavoritesOnly.IsChecked != true || p.Favorite))
            .OrderByDescending(p => p.Favorite).ThenBy(p => p.Department).ThenBy(p => p.Name).ToArray();
    }
    void AddFiles(object sender, RoutedEventArgs e)
        => ChooseFiles();
    public void ChooseFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "업무 자료|*.hwp;*.hwpx;*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.ppt;*.pptx;*.txt;*.csv;*.png;*.jpg;*.jpeg;*.zip" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }
    void AddPaths(string[] paths)
    {
        if (pending is not null || sending) return;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || files.Any(f => f.Path == path)) continue;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 104_857_600 || files.Count >= 10 || files.Sum(f => new FileInfo(f.Path).Length) + info.Length > 209_715_200)
            { Feedback.Text = "파일당 100MB, 합계 200MB, 최대 10개입니다."; break; }
            files.Add(new SourceFile(path));
        }
    }
    void DropFiles(object sender, System.Windows.DragEventArgs e)
    { if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) { AddPaths(paths); e.Handled = true; } }
    void FilesDragOver(object sender, System.Windows.DragEventArgs e)
    { if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) { e.Effects = pending is null && !sending ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; } }
    void RemoveFile(object sender, RoutedEventArgs e)
    { if (pending is null && FilesList.SelectedItem is SourceFile file) files.Remove(file); }
    void Freeze()
    {
        CollectFiles.IsEnabled = false; SubmissionDate.IsEnabled = false;
        FormattingToolbar.IsEnabled = DraftListButton.IsEnabled = false;
        RecipientsButton.IsEnabled=SelfButton.IsEnabled=ScheduleSend.IsEnabled=ScheduleDate.IsEnabled=ScheduleTime.IsEnabled=DraftButton.IsEnabled=false;RefreshRecipients();
        TitleBox.IsEnabled = false; BodyBox.IsEnabled = false; PeopleList.IsEnabled = false; PeopleSearch.IsEnabled = false;
        Department.IsEnabled = false; SendAll.IsEnabled = false; AddButton.IsEnabled = false; RemoveButton.IsEnabled = false;
    }
    void Unfreeze()
    {
        CollectFiles.IsEnabled = true; SubmissionDate.IsEnabled = true;
        FormattingToolbar.IsEnabled = DraftListButton.IsEnabled = true;
        RecipientsButton.IsEnabled=SelfButton.IsEnabled=ScheduleSend.IsEnabled=DraftButton.IsEnabled=true;ScheduleChanged(this,new RoutedEventArgs());RefreshRecipients();
        TitleBox.IsEnabled = true; BodyBox.IsEnabled = true; PeopleList.IsEnabled = true; PeopleSearch.IsEnabled = true;
        Department.IsEnabled = true; SendAll.IsEnabled = true; AddButton.IsEnabled = true; RemoveButton.IsEnabled = true;
    }
    async void Send(object sender, RoutedEventArgs e)
    {
        if (main.Api is null || sending) return;
        if (pending is null && (BodyBox.Text.Length > 20_000 || !MessageFormatRules.Valid(BodyBox.Formatting, BodyBox.Text) || JsonSerializer.SerializeToUtf8Bytes(BodyBox.Formatting, Json).Length > 80_000)) { Feedback.Text = "본문은 20,000자 이내로 작성하고, 서식이 너무 많으면 일부 서식을 지워 주세요."; return; }
        if (pending is null && CollectFiles.IsChecked == true && SubmissionDate.SelectedDate is null) { Feedback.Text = "제출 마감일을 선택하세요."; return; }
        if (pending is null && (string.IsNullOrWhiteSpace(BodyBox.Text) ||
            (!people.Any(p => p.Selected) && Department.SelectedIndex <= 0 && SendAll.IsChecked != true)))
        { Feedback.Text = "본문과 받는 사람을 확인하세요."; return; }
        if (pending is null && SendAll.IsChecked == true && System.Windows.MessageBox.Show(this, $"전체 교직원 {people.Length}명에게 보내시겠습니까?", "전체 발송 확인", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        long? schedule=null;try{if(pending is null)schedule=ScheduledAt();}catch(Exception error){Feedback.Text=error.Message;return;}
        if (pending is null)
        {
            var estimate = new Pending(Guid.Empty.ToString(), TitleBox.Text.Trim(), BodyBox.Text, people.Where(p => p.Selected).Select(p => p.Id).ToArray(), Enumerable.Repeat(new string('0', 32), files.Count).ToArray(), SendAll.IsChecked == true, Department.SelectedIndex > 0 ? (string)Department.SelectedItem : null, Deadline(), schedule, BodyBox.Formatting);
            if (JsonSerializer.SerializeToUtf8Bytes(estimate, Json).Length > 131_072) { Feedback.Text = "본문과 서식이 전송 한도를 넘습니다. 내용을 나누거나 일부 서식을 지워 주세요."; return; }
        }
        sending = true; SendButton.IsEnabled = false; Freeze();
        try
        {
            if (pending is null)
            {
                // An older server must not silently interpret a scheduled request as immediate delivery.
                if(schedule is not null)await main.Api.Get<JsonElement[]>("api/messages/scheduled");
                // Previous partial uploads are discarded before starting a new upload attempt.
                foreach (var old in uploaded) await main.Api.Send<JsonElement>(HttpMethod.Delete, $"api/attachments/{old.Id}");
                uploaded.Clear();
                foreach (var file in files) { Feedback.Text = $"첨부 업로드 중: {file.Description}"; uploaded.Add(await main.Api.Upload(file.Path)); SaveDraft(); }
                pending = new Pending(Guid.NewGuid().ToString(), TitleBox.Text.Trim(), BodyBox.Text, people.Where(p => p.Selected).Select(p => p.Id).ToArray(), uploaded.Select(f => f.Id).ToArray(), SendAll.IsChecked == true, Department.SelectedIndex > 0 ? (string)Department.SelectedItem : null, Deadline(),schedule,BodyBox.Formatting);
                SaveDraft();
            }
            Feedback.Text = "서버 저장 확인 중…";
            var result=await main.Api.Send<JsonElement>(HttpMethod.Post, "api/messages", pending);
            var collection=pending.SubmissionDeadline is not null&&pending.ScheduledAt is null;
            sent = true; File.Delete(draftPath); Close();
            if(collection)new WorkWindow(main,result.GetProperty("id").GetString()).Show();
        }
        catch (Exception error)
        {
            if(error is ApiRequestException rejected&&(int)rejected.StatusCode is 400 or 401 or 403 or 404 or 413 or 415 or 422)pending=null;
            Feedback.Text = error.Message + (pending is null ? "" : "\n같은 내용으로 다시 보내면 중복 저장되지 않습니다.");
            if (pending is null) Unfreeze();
            SaveDraft();
        }
        finally { sending = false; SendButton.IsEnabled = true;if(pending is null&&!sent)Unfreeze(); }
    }
    long? Deadline() => CollectFiles.IsChecked == true && SubmissionDate.SelectedDate is { } day ? new DateTimeOffset(DateTime.SpecifyKind(day.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Unspecified), TimeSpan.FromHours(9)).ToUnixTimeMilliseconds() : null;
    void SaveDraftClick(object sender,RoutedEventArgs e)
    {try{if(string.IsNullOrWhiteSpace(TitleBox.Text)&&string.IsNullOrWhiteSpace(BodyBox.Text)&&files.Count==0){Feedback.Text="임시저장할 내용을 입력하세요.";return;}SaveDraft();Feedback.Text="임시저장했습니다. 임시저장 목록에서 다시 열 수 있습니다.";}catch(Exception error){Feedback.Text=error.Message;}}
    void SaveDraft()
    {
        if(pending is null&&string.IsNullOrWhiteSpace(TitleBox.Text)&&string.IsNullOrWhiteSpace(BodyBox.Text)&&files.Count==0){File.Delete(draftPath);return;}
        var draft = new Draft(server, TitleBox.Text, pending?.Body ?? BodyBox.Text, people.Where(p => p.Selected).Select(p => p.Id).ToArray(),
            Department.SelectedIndex > 0 ? (string)Department.SelectedItem : null, SendAll.IsChecked == true, files.ToArray(), uploaded.ToArray(), pending, Deadline(),pending?.ScheduledAt,ScheduleSend.IsChecked==true,ScheduleDate.SelectedDate?.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),ScheduleTime.Text,CollectFiles.IsChecked==true,SubmissionDate.SelectedDate?.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),pending is not null ? pending.Format : BodyBox.Formatting,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var temporary = draftPath + ".tmp";
        File.WriteAllBytes(temporary,ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(draft,Json),null,DataProtectionScope.CurrentUser)); File.Move(temporary, draftPath, true); UpdateDraftCount();
    }
    void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (sending && !sent) { e.Cancel = true; return; }
        main.Favorites = people.Where(p => p.Favorite).Select(p => p.Id).ToHashSet();
        if (!sent)try{SaveDraft();}catch(Exception error){e.Cancel=true;Feedback.Text="임시저장하지 못했습니다: "+error.Message;}
    }
    void DiscardDraft(object sender, RoutedEventArgs e)
    {
        if (sending) return;
        var text = pending is null ? "임시 저장한 메시지를 삭제하시겠습니까?" : "이미 서버에 저장됐을 수 있습니다. 보낸 메시지에서 확인했습니까? 임시 저장을 삭제하면 새 메시지를 작성합니다.";
        if (System.Windows.MessageBox.Show(this, text, "임시 저장 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        pending = null; uploaded.Clear(); files.Clear(); TitleBox.Clear(); BodyBox.Clear();
        foreach (var person in people) person.Selected = false;
        Department.SelectedIndex = 0; SendAll.IsChecked = false; CollectFiles.IsChecked = false;ScheduleSend.IsChecked=false; Unfreeze();
        File.Delete(draftPath); draftPath = NewDraftPath(); UpdateDraftCount(); Feedback.Text = "새 메시지를 작성하세요.";
        foreach(var person in people)if(SendAll.IsChecked==true||Department.SelectedIndex>0&&person.Department==(string)Department.SelectedItem)person.Selected=true;
        Department.SelectedIndex=0;SendAll.IsChecked=false;RefreshRecipients();
    }
}
record SourceFile(string Path) { public string Description => System.IO.Path.GetFileName(Path); }
record Pending(string ClientId, string Title, string Body, string[] RecipientIds, string[] AttachmentIds, bool All, string? Department, long? SubmissionDeadline = null,long? ScheduledAt=null,[property: System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] MessageFormat? Format=null);
record Draft(string Server, string Title, string Body, string[] RecipientIds, string? Department, bool All, SourceFile[] Files, Attachment[] Uploaded, Pending? Pending, long? SubmissionDeadline = null,long? ScheduledAt=null,bool ReserveRequested=false,string? ReserveDate=null,string? ReserveTime=null,bool CollectRequested=false,string? CollectionDate=null,MessageFormat? Format=null,long SavedAt=0);
