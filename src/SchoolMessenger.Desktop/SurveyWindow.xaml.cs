using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class SurveyWindow : Window
{
    readonly MainWindow main;
    readonly Person[] people;
    readonly List<(TextBox Text, ComboBox Kind, CheckBox Required, TextBox Options, Border Card)> questions = [];
    readonly List<Control> answers = [];
    string[] targetIds = [];
    SurveyDetail? current;
    bool busy, suppressSelection;
    int detailVersion, listVersion;
    string? desiredSurveyId;
    string box = "received";
    SurveyItem[] surveys = [];
    string Me => main.Api!.Session!.User!.Id;
    public SurveyWindow(MainWindow main, Person[] people, string[]? targets = null)
    {
        this.main = main; this.people = people; main.RegisterWorkWindow(this); InitializeComponent(); StatusTone.Watch(Feedback); StatusTone.Watch(CreateFeedback); UpdateTabs();
        DeadlineDate.SelectedDate = DateTime.Today.AddDays(7);
        SendAll.Visibility = main.Api!.Session!.User is { IsAdmin: true } or { CanBroadcast: true } ? Visibility.Visible : Visibility.Collapsed;
        AddQuestion(); if (targets is not null) { targetIds = targets; TargetLabel.Text = string.Join(", ", people.Where(p => targets.Contains(p.Id)).Select(p => p.Label)); ShowCreate(true); }
        main.SurveyChanged += Changed; Closed += (_, _) => main.SurveyChanged -= Changed;
        Loaded += async (_, _) => await Run(() => Reload());
    }
    async Task Run(Func<Task> action) { try { await action(); } catch (Exception error) { Feedback.Text = error.Message; } }
    async void Changed(string id) => await Run(async () => { await Reload(); if (current?.Id == id && OwnSurvey) await RenderResults(); });
    bool OwnSurvey => current?.OwnerId == Me;
    void UpdateTabs() { foreach (var tab in new[] { ReceivedTab, SentTab }) tab.Tag = tab.Uid == box ? "selected" : null; }
    async void ChangeBox(object sender, RoutedEventArgs e)
    {
        box = ((Button)sender).Uid; UpdateTabs(); current = null; desiredSurveyId = null; Feedback.Text = "";
        await Run(() => Reload());
    }
    void ShowCreate(bool show) { BrowsePane.Visibility = show ? Visibility.Collapsed : Visibility.Visible; CreatePane.Visibility = show ? Visibility.Visible : Visibility.Collapsed; CreateFeedback.Text = ""; }
    void CancelCreate(object sender, RoutedEventArgs e) => ShowCreate(false);
    public async Task Reload(string? select = null)
    {
        if (main.Api is null) return;
        if (select is not null) desiredSurveyId = select;
        var version = ++listVersion;
        var all = await main.Api.Get<SurveyItem[]>("api/surveys");
        if (version != listVersion) return;
        surveys = all.Where(s => box == "sent" ? s.OwnerId == Me : s.Target || s.OwnerId != Me).Select(s => s with { Sent = box == "sent" }).ToArray();
        var empty = surveys.Length == 0;
        BrowseBody.Visibility = empty ? Visibility.Collapsed : Visibility.Visible; EmptyHost.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) { var start = new Button { Content = "새 설문" }; start.SetResourceReference(StyleProperty, "PrimaryButton"); start.Click += NewSurvey; EmptyHost.Content = box == "sent" ? AdminUi.Empty("보낸 설문이 없습니다", "새 설문을 만들면 응답 결과를 여기에서 봅니다.", start) : AdminUi.Empty("받은 설문이 없습니다", "동료가 보낸 설문이 여기에 표시됩니다."); }
        var id = desiredSurveyId ?? (SurveyList.SelectedItem as SurveyItem)?.Id;
        suppressSelection = true; SurveyList.ItemsSource = surveys; SurveyList.SelectedItem = surveys.FirstOrDefault(s => s.Id == id) ?? surveys.FirstOrDefault(); suppressSelection = false;
        if (current is not null && !current.Closed && surveys.Any(s => s.Id == current.Id && s.Closed))
        { current = current with { Closed = true }; AnswerButton.IsEnabled = false; CloseButton.Visibility = Visibility.Collapsed; foreach (var field in answers) field.IsEnabled = false; DeadlineLabel.Text += " · 종료"; }
        if (SurveyList.SelectedItem is SurveyItem chosen && (select is not null || current is null)) await ShowSurvey(chosen.Id);
    }
    async void SelectSurvey(object sender, SelectionChangedEventArgs e)
    { if (!suppressSelection && SurveyList.SelectedItem is SurveyItem survey) await Run(() => ShowSurvey(survey.Id)); }
    public async Task ShowSurvey(string id)
    {
        desiredSurveyId = id;
        var version = ++detailVersion;
        var detail = await main.Api!.Get<SurveyDetail>($"api/surveys/{id}"); if (version != detailVersion) return;
        current = detail; Heading.Text = current.Title; Description.Text = current.Description; Description.Visibility = current.Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Tabs.SelectedIndex = box == "sent" ? 1 : 0;
        AnswerTag.Visibility = ResultTag.Visibility = ResultNote.Visibility = current.Anonymous ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = current.Anonymous ? "익명 설문입니다. 누가 어떤 답을 했는지 게시자도, 서버 관리자도 알 수 없습니다. 제출한 답은 고칠 수 없습니다." : "기명 설문입니다. 작성자는 응답자와 답변을 확인할 수 있습니다.";
        AnswerButton.Content = current.Anonymous ? "익명으로 제출" : current.Answered ? "응답 수정" : "응답 저장";
        DeadlineLabel.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds(current.Deadline).ToLocalTime():yyyy.MM.dd HH:mm} 마감 · {(current.Closed ? "종료" : "진행 중")}";
        AnswerButton.IsEnabled = current.CanAnswer && !current.Closed; AnswerButton.Visibility = current.Anonymous && current.Answered ? Visibility.Collapsed : Visibility.Visible;
        AnswerPanel.Children.Clear(); answers.Clear();
        if (current.Anonymous && current.Answered) { var done = AdminUi.Label("응답을 제출했습니다. 익명 설문이라 제출한 내용은 다시 볼 수 없습니다.", "Ink"); done.Margin = new Thickness(0, 16, 0, 0); AnswerPanel.Children.Add(done); }
        else
        for (var i = 0; i < current.Questions.Length; i++)
        {
            var question = current.Questions[i]; var value = current.Answers?.ElementAtOrDefault(i) ?? "";
            AnswerPanel.Children.Add(new TextBlock { Text = $"{i + 1}. {question.Text}" + (question.Required ? " *" : ""), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8) });
            Control field;
            if (question.Kind == "choice") { var choices = new StackPanel(); var group = "q" + i; foreach (var option in question.Options) choices.Children.Add(new RadioButton { Content = option, GroupName = group, IsChecked = option == value, Margin = new Thickness(0, 4, 0, 4) }); field = new ContentControl { Content = choices, Focusable = false, IsTabStop = false }; }
            else field = new TextBox { Text = value, MaxLength = 2000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
            field.IsEnabled = current.CanAnswer && !current.Closed; answers.Add(field); AnswerPanel.Children.Add(field);
        }
        if (OwnSurvey) await RenderResults(); else ResultsPanel.Children.Clear();
    }
    void NewSurvey(object sender, RoutedEventArgs e) => ShowCreate(true);
    void ChooseTargets(object sender, RoutedEventArgs e)
    { var picker = new RecipientWindow(main, people, false, targetIds); if (picker.ShowDialog() == true) { targetIds = picker.SelectedIds; TargetLabel.Text = string.Join(", ", people.Where(p => targetIds.Contains(p.Id)).Select(p => p.Label)); } }
    void AddQuestionClick(object sender, RoutedEventArgs e) => AddQuestion();
    internal void ResetQuestionsForVerification() { questions.Clear(); QuestionsPanel.Children.Clear(); }
    internal void NewSurveyForVerification() => ShowCreate(true);
    public void AddQuestion(string text = "", string kind = "choice", string[]? options = null)
    {
        if (questions.Count >= 20) { CreateFeedback.Text = "문항은 최대 20개입니다."; return; }
        var panel = new StackPanel(); var card = AdminUi.Card(panel, 16); card.Margin = new Thickness(0, 8, 0, 0);
        var header = new DockPanel(); var remove = new Button { Content = "삭제", HorizontalAlignment = HorizontalAlignment.Right }; remove.SetResourceReference(StyleProperty, "SubtleButton"); DockPanel.SetDock(remove, Dock.Right); header.Children.Add(remove); header.Children.Add(new TextBlock { Text = "문항", FontWeight = FontWeights.SemiBold }); panel.Children.Add(header);
        var title = new TextBox { Text = text, MaxLength = 500, Margin = new Thickness(0, 8, 0, 8), ToolTip = "질문 내용" }; panel.Children.Add(title);
        var row = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(row);
        var type = new ComboBox { ItemsSource = new[] { "객관식", "주관식" }, SelectedIndex = kind == "text" ? 1 : 0, Width = 130 }; row.Children.Add(type);
        var required = new CheckBox { Content = "필수 응답", IsChecked = true, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(required);
        var hint = new TextBlock { Text = "선택지 · 한 줄에 하나씩", FontSize = 11, Margin = new Thickness(0, 10, 0, 6) }; panel.Children.Add(hint);
        var choices = new TextBox { Text = string.Join("\n", options ?? new[] { "찬성", "반대" }), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 65, MaxLength = 4200 }; panel.Children.Add(choices);
        void SetKind() => hint.Visibility = choices.Visibility = type.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        type.SelectionChanged += (_, _) => SetKind(); SetKind();
        questions.Add((title, type, required, choices, card)); QuestionsPanel.Children.Add(card);
        remove.Click += (_, _) => { questions.RemoveAll(q => q.Card == card); QuestionsPanel.Children.Remove(card); };
    }
    async void CreateClick(object sender, RoutedEventArgs e) => await Run(CreateSurvey);
    public async Task CreateSurvey()
    {
        if (busy) return;
        if (!TimeSpan.TryParseExact(DeadlineTime.Text.Trim(), @"hh\:mm", null, out var time) || DeadlineDate.SelectedDate is not DateTime date) { CreateFeedback.Text = "마감일과 시간을 확인하세요. 예: 17:00"; return; }
        if (SendAll.IsChecked == true && Notice.Show(this, "전체 교직원에게 설문을 보내시겠습니까?", "전체 발송", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var qs = questions.Select(q => new SurveyQuestion(q.Text.Text.Trim(), q.Kind.SelectedIndex == 0 ? "choice" : "text", q.Required.IsChecked == true,
            q.Kind.SelectedIndex == 0 ? q.Options.Text.Split('\n').Select(o => o.Trim()).Where(o => o.Length > 0).ToArray() : [])).ToArray();
        busy = true; CreateButton.IsEnabled = false;
        try
        {
            var result = await main.Api!.Send<JsonElement>(HttpMethod.Post, "api/surveys", new { title = TitleBox.Text, description = DescriptionBox.Text, deadline = new DateTimeOffset(date.Date + time).ToUnixTimeMilliseconds(), questions = qs, targetIds, all = SendAll.IsChecked == true, anonymous = AnonymousBox.IsChecked == true });
            var id = result.GetProperty("id").GetString()!;
            box = "sent"; UpdateTabs(); ShowCreate(false); await Reload(id); Feedback.Text = "설문을 보냈습니다.";
            TitleBox.Clear(); DescriptionBox.Clear(); AnonymousBox.IsChecked = false; questions.Clear(); QuestionsPanel.Children.Clear(); AddQuestion();
        }
        catch (Exception error) { CreateFeedback.Text = error.Message; }
        finally { busy = false; CreateButton.IsEnabled = true; }
    }
    async void SaveAnswer(object sender, RoutedEventArgs e) => await Run(() => SubmitAnswers());
    public async Task SubmitAnswers(string[]? supplied = null)
    {
        if (current is null || busy) return; busy = true; AnswerButton.IsEnabled = false;
        try
        {
            var values = supplied ?? answers.Select(a => a is TextBox text ? text.Text : ((StackPanel)((ContentControl)a).Content).Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Content as string ?? "").ToArray();
            await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/surveys/{current.Id}/answers", new { answers = values });
            var nameless = current.Anonymous; await ShowSurvey(current.Id); await Reload(); Feedback.Text = nameless ? "익명으로 제출했습니다." : "응답을 저장했습니다. 마감 전에는 수정할 수 있습니다.";
        }
        finally { busy = false; AnswerButton.IsEnabled = current?.CanAnswer == true && !current.Closed; }
    }
    async Task<SurveyResults> Results(string id)
    { var result = await main.Api!.Get<SurveyResults>($"api/surveys/{id}/results"); return result.Anonymous ? await main.Archive.KeepBallots(result, id) : result; }
    async Task RenderResults()
    {
        if (current is null) return;
        var id = current.Id; var result = await Results(id); if (current?.Id != id) return;
        ResultsPanel.Children.Clear();
        var item = surveys.FirstOrDefault(s => s.Id == id);
        ResultTitle.Text = current.Title; CloseButton.Visibility = current.Closed ? Visibility.Collapsed : Visibility.Visible;
        var answered = Math.Max(item?.ResponseCount ?? 0, result.Responses.Length);
        ResultSummary.Text = $"응답 {answered} / {item?.TargetCount ?? answered}명 · {DateTimeOffset.FromUnixTimeMilliseconds(current.Deadline).ToLocalTime():M.d HH:mm} 마감{(current.Closed ? " · 종료" : "")}{(result.Anonymous ? " · 응답자 이름은 저장되지 않습니다" : "")}{(result.Responses.Length < answered ? $" · 이 PC에 저장된 응답 {result.Responses.Length}개만 보입니다" : "")}";
        var block = ResultsPanel;
        void Line(string text, bool bold = false) { var line = AdminUi.Label(text, "Ink", 13, bold); line.Margin = new Thickness(0, 4, 0, 4); block.Children.Add(line); }
        // A choice reads as a share of the answers: label and count above a bar of that width.
        void Bar(string option, int count, int total)
        {
            var row = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            var label = new DockPanel(); var number = new TextBlock { Text = total == 0 ? "0명" : $"{count}명 · {Math.Round(100.0 * count / total)}%", FontSize = 11 }; number.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            DockPanel.SetDock(number, Dock.Right); label.Children.Add(number); label.Children.Add(new TextBlock { Text = option, TextTrimming = TextTrimming.CharacterEllipsis }); row.Children.Add(label);
            var track = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 0) }; track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(total == 0 ? 1 : Math.Max(0, total - count), GridUnitType.Star) });
            var back = new Border { CornerRadius = new CornerRadius(4) }; back.SetResourceReference(Border.BackgroundProperty, "Hover"); Grid.SetColumnSpan(back, 2); track.Children.Add(back);
            if (count > 0) { var fill = new Border { CornerRadius = new CornerRadius(4) }; fill.SetResourceReference(Border.BackgroundProperty, "Blue"); track.Children.Add(fill); }
            row.Children.Add(track); block.Children.Add(row);
        }
        for (var i = 0; i < result.Questions.Length; i++)
        {
            var q = result.Questions[i]; block = new StackPanel(); var card = AdminUi.Card(block, 16); card.Margin = new Thickness(0, 0, 0, 12); ResultsPanel.Children.Add(card);
            Line($"{i + 1}. {q.Text}", true);
            if (q.Kind == "choice") foreach (var (option, count) in q.Options.Select(o => (o, result.Responses.Count(r => r.Answers[i] == o))).OrderByDescending(x => x.Item2)) Bar(option, count, result.Responses.Length);
            // Names only ever appear on a named survey; the choice of an anonymous survey is the bar above.
            var written = result.Responses.Where(r => r.Answers[i].Length > 0 && !(result.Anonymous && q.Kind == "choice")).ToArray();
            foreach (var response in written) Line(result.Anonymous ? "· " + response.Answers[i] : $"{response.Name} · {response.Department}: {response.Answers[i]}");
            if (q.Kind == "text" && written.Length == 0) { var none = AdminUi.Label("아직 응답이 없습니다.", "Faint", 12); none.Margin = new Thickness(0, 4, 0, 4); block.Children.Add(none); }
        }
    }
    async void CloseSurvey(object sender, RoutedEventArgs e)
    {
        if (current is null || Notice.Show(this, "설문을 종료하면 응답을 받거나 수정할 수 없습니다. 종료하시겠습니까?", "설문 종료", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(async () => { await main.Api!.Send<JsonElement>(HttpMethod.Post, $"api/surveys/{current.Id}/close"); await ShowSurvey(current.Id); await Reload(); });
    }
    public static string Csv(SurveyResults results)
    {
        static string Cell(string value) { if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        var who = results.Anonymous ? [] : new[] { "이름", "부서", "응답 시각" };
        var rows = new List<string> { string.Join(",", who.Concat(results.Questions.Select(q => q.Text)).Select(Cell)) };
        rows.AddRange(results.Responses.Select(r => string.Join(",", (results.Anonymous ? [] : new[] { r.Name, r.Department, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm") }).Concat(r.Answers).Select(Cell))));
        return string.Join("\r\n", rows);
    }
    async void ExportResults(object sender, RoutedEventArgs e)
    {
        if (current is null) return; var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "설문결과.csv", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog(this) == true) await Run(async () => { var result = await Results(current.Id); File.WriteAllText(dialog.FileName, Csv(result), new UTF8Encoding(true)); Feedback.Text = "CSV 파일을 저장했습니다."; });
    }
}
