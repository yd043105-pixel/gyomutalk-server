using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SchoolMessenger.Desktop;

// 수강신청 for the curriculum duty: rounds and their results, the student roster, and the one-time Google connection.
// Students answer on the school's own Apps Script page; this window only talks to the school server.
public sealed class CourseWindow : Window
{
    public sealed record Round(string Id, string Title, int Grade, long Closes, long? PublishedAt, long? PulledAt, int Students, int Submitted);
    public sealed record Student(string Id, int Grade, int ClassNo, int Number, string Name);
    public sealed record Group(string Name, int Pick, string[] Courses);
    public sealed record Setup(bool Connected, string ScriptUrl, bool ScriptCurrent, GradeCount[] Grades);
    public sealed record GradeCount(int Grade, int Count);
    public sealed record Course(string Name, int Count);
    public sealed record Tally(string Name, int Pick, Course[] Courses);
    public sealed record Answer(string Id, int ClassNo, int Number, string Name, bool Linked, string[][]? Picks, long? SubmittedAt);
    public sealed record Detail(string Id, string Title, int Grade, long Closes, Group[] Groups, long? PublishedAt, long? PulledAt);
    public sealed record Results(Detail Round, Tally[] Groups, Answer[] Students);
    public sealed record StudentLink(int Grade, int ClassNo, int Number, string Name, string Link);
    public sealed record Row(int Grade, int ClassNo, int Number, string Name);
    public sealed record Read(Row[] Students, int Skipped, string Columns);

    readonly Api api;
    readonly StackPanel body = new() { Margin = new Thickness(24, 16, 24, 24) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 8, 24, 12), FontSize = 12 };
    bool busy; string tab = "rounds"; int rosterGrade; int rosterClass;
    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");

    public CourseWindow(MainWindow main)
    {
        api = main.Api!; main.RegisterWorkWindow(this); Resources = main.Resources;
        Title = "교무톡 · 수강신청"; Width = 960; Height = 760; MinWidth = 700; MinHeight = 500; FontSize = 13;
        Style = (Style)FindResource("SchoolWindow"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel(); DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        dock.SetResourceReference(BackgroundProperty, "Surface"); Content = dock;
        Loaded += async (_, _) => await Run(Open);
    }

    // One action at a time; a slow one says what it is doing, and a failure stays on screen with what to do next.
    async Task Run(Func<Task> action, string? doing = null)
    {
        if (busy) return; busy = true; body.IsEnabled = false;
        Say(doing ?? "", false);
        try { await action(); if (doing is not null && status.Text == doing) Say("", false); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException) { Say(e.Message, true); }
        finally { busy = false; body.IsEnabled = true; }
    }
    void Say(string text, bool problem) { status.Text = text; status.SetResourceReference(TextBlock.ForegroundProperty, problem ? "Danger" : "Muted"); }
    Task Open() => tab switch { "roster" => Roster(), "connect" => Connect(), _ => Rounds() };

    void Header(string? actionLabel = null, Func<Task>? action = null)
    {
        body.Children.Clear();
        var head = new DockPanel();
        if (actionLabel is not null)
        {
            var primary = new Button { Content = actionLabel, VerticalAlignment = VerticalAlignment.Center }; primary.SetResourceReference(StyleProperty, "PrimaryButton");
            primary.Click += async (_, _) => await Run(action!); DockPanel.SetDock(primary, Dock.Right); head.Children.Add(primary);
        }
        head.Children.Add(AdminUi.Heading("수강신청", 22)); body.Children.Add(head);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 6, 0, 0) };
        foreach (var (key, label) in new[] { ("rounds", "신청 · 결과"), ("roster", "학생 명단"), ("connect", "구글 연결") })
        {
            var button = new Button { Content = label, Tag = key == tab ? "selected" : null }; button.SetResourceReference(StyleProperty, "TabButton");
            button.Click += async (_, _) => { tab = key; await Run(Open); }; tabs.Children.Add(button);
        }
        body.Children.Add(tabs);
        var rule = new Border { Height = 1, Margin = new Thickness(-24, 0, -24, 18) }; rule.SetResourceReference(Border.BackgroundProperty, "Line"); body.Children.Add(rule);
    }
    static Button Plain(string label, Func<Task> action, CourseWindow owner)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        button.Click += async (_, _) => await owner.Run(action); return button;
    }
    static string Day(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms - 1).ToOffset(TimeSpan.FromHours(9)).ToString("M월 d일(ddd)", Korean);
    static string When(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(TimeSpan.FromHours(9)).ToString("M/d HH:mm", Korean);
    static bool IsClosed(long closes) => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= closes;

    // ── Rounds ──
    async Task Rounds()
    {
        var setup = await api.Get<Setup>("api/courses/setup"); var rounds = await api.Get<Round[]>("api/courses/rounds");
        Header(setup.Connected ? "새 신청 만들기" : null, () => Editor(null));
        if (setup.Connected && !setup.ScriptCurrent) body.Children.Add(Warning("교무톡이 업데이트되어 구글 스크립트를 새로 붙여넣어야 합니다. ‘구글 연결’에서 코드를 다시 복사해 새 버전으로 배포하세요."));
        if (!setup.Connected)
        {
            var go = new Button { Content = "구글 연결하기" }; go.SetResourceReference(StyleProperty, "PrimaryButton"); go.Click += async (_, _) => { tab = "connect"; await Run(Open); };
            body.Children.Add(AdminUi.EmptyCard("먼저 구글을 연결하세요", "학생 신청 화면은 학교 구글 계정의 앱스스크립트에서 열립니다. 처음 한 번만 연결하면 됩니다.", go)); return;
        }
        if (rounds.Length == 0)
        {
            var add = new Button { Content = "새 신청 만들기" }; add.SetResourceReference(StyleProperty, "PrimaryButton"); add.Click += async (_, _) => await Run(() => Editor(null));
            body.Children.Add(AdminUi.EmptyCard("아직 만든 신청이 없습니다", setup.Grades.Length == 0 ? "‘학생 명단’에 나이스 명단을 먼저 올린 뒤, 선택군과 과목을 넣어 신청을 만드세요." : "선택군과 과목을 넣어 신청을 만들고 학생 링크를 보내세요.", add)); return;
        }
        foreach (var round in rounds)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel(); var title = AdminUi.Heading(round.Title, 15); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap; words.Children.Add(title);
            var meta = AdminUi.Label($"{round.Grade}학년 · {Day(round.Closes)}까지", "Muted", 12); meta.Margin = new Thickness(0, 4, 0, 0); words.Children.Add(meta); row.Children.Add(words);
            var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(side, 1);
            var (text, tone) = round.PublishedAt is null ? ("링크 보내기 전", "Neutral") : IsClosed(round.Closes) ? ("마감", "Neutral") : ("신청 받는 중", "Success");
            side.Children.Add(AdminUi.Badge(text, tone));
            var count = AdminUi.Label(round.PublishedAt is null ? "" : $"제출 {round.Submitted} / {round.Students}명", "Ink", 13, true); count.Margin = new Thickness(14, 0, 4, 0); count.VerticalAlignment = VerticalAlignment.Center; side.Children.Add(count);
            row.Children.Add(side);
            var open = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
            AutomationProperties.SetName(open, round.Title + " 결과 보기"); open.Click += async (_, _) => await Run(() => ShowRound(round.Id, round.PublishedAt is not null), round.PublishedAt is not null ? "구글에서 제출 내용을 가져오는 중…" : null);
            body.Children.Add(open);
        }
    }
    Border Warning(string text)
    {
        var note = new Border { Child = AdminUi.Label(text, "Warning", 13), Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 14) };
        note.SetResourceReference(Border.BackgroundProperty, "WarningTint"); return note;
    }

    // ── One round: what was chosen, and who has not answered ──
    async Task ShowRound(string id, bool pull)
    {
        Results results;
        if (pull)
        {
            try { results = await api.Send<Results>(HttpMethod.Post, $"api/courses/rounds/{id}/refresh"); }
            catch (InvalidOperationException e) { results = await api.Get<Results>($"api/courses/rounds/{id}/results"); Draw(results); Say(e.Message + " 마지막으로 가져온 내용을 보여 줍니다.", true); return; }
        }
        else results = await api.Get<Results>($"api/courses/rounds/{id}/results");
        Draw(results);
    }
    void Draw(Results results)
    {
        var round = results.Round; var answered = results.Students.Count(s => s.Picks is not null); var total = results.Students.Length;
        Header();
        var back = new Button { Content = "‹  신청 목록으로", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) }; back.SetResourceReference(StyleProperty, "SubtleButton");
        back.Click += async (_, _) => await Run(Rounds); body.Children.Add(back);
        body.Children.Add(AdminUi.Heading(round.Title, 20));
        var meta = AdminUi.Label($"{round.Grade}학년 · {Day(round.Closes)}까지" + (round.PulledAt is { } at ? $" · {When(at)}에 가져옴" : ""), "Muted", 13); meta.Margin = new Thickness(0, 4, 0, 12); body.Children.Add(meta);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var send = new Button { Content = round.PublishedAt is null ? "학생 링크 만들기" : "링크 다시 보내기", Margin = new Thickness(0, 0, 8, 8), ToolTip = "명단에 새로 들어온 학생에게도 링크를 만들고, 바뀐 내용을 학생 화면에 반영합니다. 이미 나눠 준 링크는 그대로입니다." };
        if (round.PublishedAt is null) send.SetResourceReference(StyleProperty, "PrimaryButton");
        send.Click += async (_, _) => await Run(async () =>
        {
            var sent = await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, $"api/courses/rounds/{round.Id}/publish");
            await ShowRound(round.Id, false); Say($"학생 {sent.GetProperty("students").GetInt32()}명의 링크를 구글에 올렸습니다. ‘링크 목록 저장’으로 담임 선생님께 나눠 주세요.", false);
        }, "구글에 신청 내용을 올리는 중…");
        tools.Children.Add(send);
        if (round.PublishedAt is not null)
        {
            tools.Children.Add(Plain("결과 새로고침", () => ShowRound(round.Id, true), this));
            tools.Children.Add(Plain("링크 목록 저장", () => SaveLinks(round), this));
            tools.Children.Add(Plain("결과 저장", () => SaveResults(results), this));
        }
        tools.Children.Add(Plain("수정", () => Editor(round), this));
        var remove = new Button { Content = "삭제", Margin = new Thickness(0, 0, 8, 8) }; remove.SetResourceReference(StyleProperty, "DangerButton");
        remove.Click += async (_, _) => await Run(async () =>
        {
            if (Notice.Show(this, $"‘{round.Title}’ 신청을 지울까요?\n\n학생 링크가 더 이상 열리지 않고, 받은 제출 내용도 함께 지워집니다. 되돌릴 수 없으니 필요하면 먼저 ‘결과 저장’을 누르세요.", "신청 삭제", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, $"api/courses/rounds/{round.Id}/delete"); await Rounds(); Say("신청을 지웠습니다.", false);
        }, "구글에서 신청을 내리는 중…");
        tools.Children.Add(remove); body.Children.Add(tools);
        if (round.PublishedAt is null)
        {
            body.Children.Add(AdminUi.EmptyCard("아직 학생 링크를 만들지 않았습니다", $"‘학생 링크 만들기’를 누르면 {round.Grade}학년 학생마다 링크가 만들어져 구글에 올라갑니다.")); return;
        }
        var summary = new WrapPanel { Margin = new Thickness(0, 4, 0, 14) };
        summary.Children.Add(AdminUi.Badge($"제출 {answered}명", "Success")); var missing = AdminUi.Badge($"미제출 {total - answered}명", total - answered > 0 ? "Warning" : "Neutral"); missing.Margin = new Thickness(8, 0, 0, 0); summary.Children.Add(missing);
        body.Children.Add(summary);
        foreach (var group in results.Groups)
        {
            var stack = new StackPanel(); var name = AdminUi.Heading($"{group.Name}", 15); stack.Children.Add(name);
            var hint = AdminUi.Label($"{group.Courses.Length}과목 중 {group.Pick}과목 선택", "Muted", 12); hint.Margin = new Thickness(0, 2, 0, 10); stack.Children.Add(hint);
            // Bars are out of the students who answered, so a long bar means most of the grade chose it.
            var most = Math.Max(1, answered);
            foreach (var course in group.Courses.OrderByDescending(c => c.Count))
            {
                var line = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
                var label = AdminUi.Label(course.Name, "Ink", 13); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap; label.ToolTip = course.Name; label.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(label);
                var track = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) }; Grid.SetColumn(track, 1);
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(course.Count, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(most - course.Count, GridUnitType.Star) });
                var back2 = new Border { CornerRadius = new CornerRadius(5) }; back2.SetResourceReference(Border.BackgroundProperty, "Subtle"); Grid.SetColumnSpan(back2, 2); track.Children.Add(back2);
                var fill = new Border { CornerRadius = new CornerRadius(5) }; fill.SetResourceReference(Border.BackgroundProperty, "Blue"); if (course.Count > 0) track.Children.Add(fill);
                line.Children.Add(track);
                var count = AdminUi.Label(course.Count + "명", "Ink", 13, true); count.TextAlignment = TextAlignment.Right; count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 2); line.Children.Add(count);
                stack.Children.Add(line);
            }
            var card = AdminUi.Card(stack, 18); card.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(card);
        }
        var waiting = results.Students.Where(s => s.Picks is null).ToArray();
        var list = new StackPanel(); list.Children.Add(AdminUi.Heading(waiting.Length == 0 ? "모두 제출했습니다" : $"미제출 학생 {waiting.Length}명", 15));
        foreach (var room in waiting.GroupBy(s => s.ClassNo))
        {
            var line = new Grid { Margin = new Thickness(0, 10, 0, 0) }; line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(AdminUi.Label($"{room.Key}반 {room.Count()}명", "Muted", 13, true));
            var names = AdminUi.Label(string.Join("   ", room.Select(s => $"{s.Number} {s.Name}" + (s.Linked ? "" : "(링크 없음)"))), "Ink", 13); Grid.SetColumn(names, 1); line.Children.Add(names);
            list.Children.Add(line);
        }
        body.Children.Add(AdminUi.Card(list, 18));
    }

    async Task SaveLinks(Detail round)
    {
        var links = await api.Get<StudentLink[]>($"api/courses/rounds/{round.Id}/links");
        Save($"{round.Title} 학생 링크.csv", "학년,반,번호,이름,링크", links.Select(l => new object[] { l.Grade, l.ClassNo, l.Number, l.Name, l.Link }),
            "링크 목록을 저장했습니다. 학생 한 명마다 자기 링크만 받도록 나눠 주세요.");
    }
    Task SaveResults(Results results)
    {
        var groups = results.Round.Groups;
        Save($"{results.Round.Title} 결과.csv", "학년,반,번호,이름,제출," + string.Join(",", groups.Select(g => Cell(g.Name))) + ",제출 시각",
            results.Students.Select(s => new object[] { results.Round.Grade, s.ClassNo, s.Number, s.Name, s.Picks is null ? "미제출" : "제출" }
                .Concat(groups.Select((_, i) => (object)(s.Picks is null ? "" : string.Join(", ", s.Picks[i])))).Append(s.SubmittedAt is { } at ? When(at) : "").ToArray()), "결과를 저장했습니다.");
        return Task.CompletedTask;
    }
    static string Cell(object value) { var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text; }
    void Save(string name, string header, System.Collections.Generic.IEnumerable<object[]> rows, string done)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = string.Concat(name.Split(Path.GetInvalidFileNameChars())), Filter = "엑셀에서 여는 CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        // The byte order mark lets Excel read Korean correctly.
        File.WriteAllText(dialog.FileName, header + "\r\n" + string.Join("\r\n", rows.Select(r => string.Join(",", r.Select(Cell)))) + "\r\n", new UTF8Encoding(true));
        Say(done, false);
    }

    // ── New or changed round ──
    async Task Editor(Detail? round)
    {
        var setup = await api.Get<Setup>("api/courses/setup");
        Header();
        body.Children.Add(AdminUi.Heading(round is null ? "새 신청 만들기" : "신청 수정", 20));
        var form = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, Width = 640 };
        body.Children.Add(form);
        TextBlock Caption(string text) { var c = AdminUi.Label(text, "Muted", 13); c.Margin = new Thickness(0, 14, 0, 6); return c; }
        form.Children.Add(Caption("신청 이름"));
        var title = new TextBox { Text = round?.Title ?? $"{DateTime.Today.Year + 1}학년도 선택과목 신청", MaxLength = 80 }; AutomationProperties.SetName(title, "신청 이름"); form.Children.Add(title);
        var pair = new Grid(); pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); pair.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 2); pair.Children.Add(left); pair.Children.Add(right); form.Children.Add(pair);
        left.Children.Add(Caption("신청하는 학년"));
        var grade = new ComboBox { ItemsSource = new[] { 1, 2, 3 }.Select(g => $"{g}학년 ({setup.Grades.FirstOrDefault(c => c.Grade == g)?.Count ?? 0}명)").ToArray(), SelectedIndex = (round?.Grade ?? setup.Grades.Select(g => g.Grade).DefaultIfEmpty(1).First()) - 1 };
        AutomationProperties.SetName(grade, "신청하는 학년"); left.Children.Add(grade);
        right.Children.Add(Caption("마감일 (그날 밤 12시까지)"));
        var closes = new DatePicker { SelectedDate = round is null ? DateTime.Today.AddDays(7) : DateTimeOffset.FromUnixTimeMilliseconds(round.Closes - 1).ToOffset(TimeSpan.FromHours(9)).Date, DisplayDateStart = DateTime.Today.AddDays(-1) };
        AutomationProperties.SetName(closes, "마감일"); right.Children.Add(closes);
        form.Children.Add(Caption("선택군 · 과목"));
        var groups = new StackPanel(); form.Children.Add(groups);
        void AddGroup(Group? g)
        {
            var card = new StackPanel();
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBox { Text = g?.Name ?? $"선택군 {(char)('A' + groups.Children.Count)}", MaxLength = 40, ToolTip = "선택군 이름" }; AutomationProperties.SetName(name, "선택군 이름"); top.Children.Add(name);
            var pick = new ComboBox { ItemsSource = Enumerable.Range(1, 10).Select(n => n + "과목 고르기").ToArray(), SelectedIndex = (g?.Pick ?? 1) - 1 }; AutomationProperties.SetName(pick, "고를 과목 수"); Grid.SetColumn(pick, 2); top.Children.Add(pick);
            var drop = new Button { Content = "빼기", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(drop, 3); top.Children.Add(drop); AutomationProperties.SetName(drop, "선택군 빼기");
            card.Children.Add(top);
            var note = AdminUi.Label("과목을 한 줄에 하나씩 적으세요.", "Faint", 12); note.Margin = new Thickness(0, 10, 0, 4); card.Children.Add(note);
            var courses = new TextBox { Text = g is null ? "" : string.Join("\r\n", g.Courses), AcceptsReturn = true, MinHeight = 96, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(courses, "과목 목록"); card.Children.Add(courses);
            var shell = AdminUi.Card(card, 16); shell.Margin = new Thickness(0, 0, 0, 10); shell.Tag = (name, pick, courses); groups.Children.Add(shell);
            drop.Click += (_, _) => { if (groups.Children.Count > 1) groups.Children.Remove(shell); };
        }
        foreach (var g in round?.Groups ?? [new Group("선택군 A", 1, [])]) AddGroup(g);
        var more = new Button { Content = "+ 선택군 추가", HorizontalAlignment = HorizontalAlignment.Left }; more.Click += (_, _) => AddGroup(null); form.Children.Add(more);
        if (round?.PublishedAt is not null) { var note = AdminUi.Label("저장하면 바뀐 내용이 학생 화면에 바로 반영됩니다. 제출한 학생이 있으면 학년·선택군·과목은 바꿀 수 없습니다.", "Muted", 12); note.Margin = new Thickness(0, 14, 0, 0); form.Children.Add(note); }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        var save = new Button { Content = "저장" }; save.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "취소", Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(save); actions.Children.Add(cancel); form.Children.Add(actions);
        cancel.Click += async (_, _) => await Run(() => round is null ? Rounds() : ShowRound(round.Id, false));
        save.Click += async (_, _) => await Run(async () =>
        {
            if (closes.SelectedDate is not { } day) throw new InvalidOperationException("마감일을 고르세요.");
            var list = groups.Children.OfType<Border>().Select(b => ((TextBox Name, ComboBox Pick, TextBox Courses))b.Tag)
                .Select(t => new Group(t.Name.Text, t.Pick.SelectedIndex + 1, t.Courses.Text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray())).ToArray();
            var request = new { title = title.Text, grade = grade.SelectedIndex + 1, closes = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), groups = list };
            var saved = await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, round is null ? "api/courses/rounds" : $"api/courses/rounds/{round.Id}", request);
            await ShowRound(saved.GetProperty("id").GetString()!, false); Say(round?.PublishedAt is null ? "저장했습니다. ‘학생 링크 만들기’를 누르면 학생 링크가 만들어집니다." : "저장하고 학생 화면에 반영했습니다.", false);
        }, round?.PublishedAt is null ? null : "학생 화면에 반영하는 중…");
    }

    // ── Roster ──
    async Task Roster()
    {
        var students = await api.Get<Student[]>("api/courses/students");
        Header("나이스 명단 올리기", Upload);
        var intro = AdminUi.Label("나이스에서 받은 학생 명단(.xlsx, .csv)을 올리거나, 엑셀에서 머리글(학년·반·번호·성명)까지 복사해 붙여넣으세요. 올린 학년의 명단만 바뀝니다.", "Muted", 13); intro.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(intro);
        var paste = new TextBox { AcceptsReturn = true, AcceptsTab = true, MinHeight = 90, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        AutomationProperties.SetName(paste, "붙여넣을 명단");
        var read = new Button { Content = "붙여넣은 명단 읽기", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) }; read.SetResourceReference(StyleProperty, "PrimaryButton");
        var open = new Button { Content = "붙여넣기로 넣기", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        open.Click += (_, _) => { paste.Visibility = read.Visibility = Visibility.Visible; open.Visibility = Visibility.Collapsed; paste.Focus(); };
        read.Click += async (_, _) => await Run(async () => await Confirm(await api.Send<Read>(HttpMethod.Post, "api/courses/students/read", new { text = paste.Text })), "명단을 읽는 중…");
        body.Children.Add(open); body.Children.Add(paste); body.Children.Add(read);
        if (students.Length == 0) { body.Children.Add(AdminUi.EmptyCard("학생 명단이 비어 있습니다", "나이스 명단을 올리면 학년·반·번호·이름이 들어갑니다.")); AddForm(); return; }
        var grades = students.Select(s => s.Grade).Distinct().Order().ToArray(); if (!grades.Contains(rosterGrade)) rosterGrade = grades[0];
        var classes = students.Where(s => s.Grade == rosterGrade).Select(s => s.ClassNo).Distinct().Order().ToArray(); if (!classes.Contains(rosterClass)) rosterClass = classes[0];
        WrapPanel Chips(System.Collections.Generic.IEnumerable<(string Label, bool On, Action Pick)> items)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var (label, on, pick) in items)
            {
                var chip = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6) }; chip.SetResourceReference(StyleProperty, "FilterButton");
                if (on) { chip.SetResourceReference(BackgroundProperty, "Pale"); chip.SetResourceReference(ForegroundProperty, "Blue"); chip.SetResourceReference(BorderBrushProperty, "Blue"); }
                chip.Click += async (_, _) => { pick(); await Run(Roster); }; row.Children.Add(chip);
            }
            return row;
        }
        body.Children.Add(Chips(grades.Select(g => ($"{g}학년 {students.Count(s => s.Grade == g)}명", g == rosterGrade, (Action)(() => { rosterGrade = g; rosterClass = 0; })))));
        body.Children.Add(Chips(classes.Select(c => ($"{c}반 {students.Count(s => s.Grade == rosterGrade && s.ClassNo == c)}", c == rosterClass, (Action)(() => rosterClass = c)))));
        var table = new StackPanel();
        foreach (var s in students.Where(s => s.Grade == rosterGrade && s.ClassNo == rosterClass))
        {
            var line = new Grid { Margin = new Thickness(0, 0, 0, 0) }; line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var place = AdminUi.Label($"{s.Number}번", "Muted", 13); place.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(place);
            var name = AdminUi.Label(s.Name, "Ink", 13, true); name.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(name, 1); line.Children.Add(name);
            var drop = new Button { Content = "빼기", Padding = new Thickness(10, 3, 10, 3) }; AutomationProperties.SetName(drop, s.Name + " 명단에서 빼기"); Grid.SetColumn(drop, 2); line.Children.Add(drop);
            drop.Click += async (_, _) => await Run(async () =>
            {
                if (Notice.Show(this, $"{s.Grade}학년 {s.ClassNo}반 {s.Number}번 {s.Name} 학생을 명단에서 뺄까요?\n\n받은 링크가 더 이상 열리지 않고, 제출 내용도 함께 지워집니다.", "학생 빼기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, $"api/courses/students/{s.Id}/delete"); await Roster(); Say(s.Name + " 학생을 뺐습니다. 이미 보낸 신청에는 ‘링크 다시 보내기’를 눌러야 반영됩니다.", false);
            });
            var cell = new Border { Child = line, Padding = new Thickness(16, 8, 12, 8), BorderThickness = new Thickness(0, 0, 0, 1) }; cell.SetResourceReference(Border.BorderBrushProperty, "Line"); table.Children.Add(cell);
        }
        var tableCard = AdminUi.Card(table, 0); tableCard.Margin = new Thickness(0, 4, 0, 12); body.Children.Add(tableCard);
        AddForm();
    }
    void AddForm()
    {
        var line = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        TextBox Box(string label, double width, string text = "") { var box = new TextBox { Width = width, Text = text, Margin = new Thickness(0, 0, 8, 8), ToolTip = label }; AutomationProperties.SetName(box, label); return box; }
        line.Children.Add(AdminUi.Label("한 명 더하기", "Muted", 13, true)); ((TextBlock)line.Children[0]).Margin = new Thickness(0, 6, 12, 0);
        var grade = Box("학년", 52, rosterGrade > 0 ? rosterGrade.ToString(CultureInfo.InvariantCulture) : ""); var room = Box("반", 52, rosterClass > 0 ? rosterClass.ToString(CultureInfo.InvariantCulture) : ""); var number = Box("번호", 52); var name = Box("이름", 140);
        foreach (var (box, unit) in new[] { (grade, "학년"), (room, "반"), (number, "번"), (name, "") }) { line.Children.Add(box); if (unit.Length > 0) { var u = AdminUi.Label(unit, "Muted", 13); u.Margin = new Thickness(-4, 6, 10, 0); line.Children.Add(u); } }
        var add = new Button { Content = "더하기", Margin = new Thickness(0, 0, 0, 8) };
        add.Click += async (_, _) => await Run(async () =>
        {
            int Number(TextBox box) => int.TryParse(box.Text.Trim(), out var n) ? n : 0;
            await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, "api/courses/students", new { grade = Number(grade), classNo = Number(room), number = Number(number), name = name.Text });
            rosterGrade = Number(grade); rosterClass = Number(room); await Roster(); Say(name.Text.Trim() + " 학생을 더했습니다.", false);
        });
        line.Children.Add(add); body.Children.Add(line);
    }
    async Task Upload()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "나이스에서 받은 학생 명단 선택", Filter = "학생 명단 (*.xlsx;*.csv)|*.xlsx;*.csv|옛 엑셀 (*.xls)|*.xls" };
        if (dialog.ShowDialog(this) != true) return;
        Say("명단을 읽는 중…", false);
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(dialog.FileName);
        form.Add(new StreamContent(stream), "file", Path.GetFileName(dialog.FileName));
        await Confirm(await api.Send<Read>(HttpMethod.Post, "api/courses/students/read", content: form));
    }
    async Task Confirm(Read read)
    {
        var byGrade = read.Students.GroupBy(s => s.Grade).Select(g => $"{g.Key}학년 {g.Count()}명({g.Min(s => s.ClassNo)}~{g.Max(s => s.ClassNo)}반)");
        var grades = read.Students.Select(s => s.Grade).Distinct().Order().ToArray();
        var text = $"읽은 명단: {string.Join(", ", byGrade)}\n읽은 칸: {read.Columns}" + (read.Skipped > 0 ? $"\n학생으로 읽지 않은 줄: {read.Skipped}줄(합계·빈칸 등)" : "") +
                   $"\n첫 학생: {read.Students[0].Grade}학년 {read.Students[0].ClassNo}반 {read.Students[0].Number}번 {read.Students[0].Name}" +
                   $"\n\n{string.Join("·", grades)}학년 명단을 이 내용으로 바꿀까요?\n반·번호·이름이 그대로인 학생은 받은 링크와 제출 내용이 그대로 남고, 여기에 없는 학생은 명단에서 빠집니다.";
        Say("", false);
        if (Notice.Show(this, text, "명단 넣기", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var done = await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, "api/courses/students/import", new { grades, students = read.Students });
        rosterGrade = grades[0]; rosterClass = 0; await Roster();
        Say($"명단을 넣었습니다. 새로 들어온 학생 {done.GetProperty("added").GetInt32()}명, 빠진 학생 {done.GetProperty("removed").GetInt32()}명. 이미 보낸 신청에는 ‘링크 다시 보내기’를 눌러야 반영됩니다.", false);
    }

    // ── Google connection: paste the generated script once, deploy it, paste its address back ──
    async Task Connect()
    {
        var setup = await api.Get<Setup>("api/courses/setup");
        Header();
        var state = setup.Connected ? setup.ScriptCurrent ? AdminUi.Badge("연결됨", "Success") : AdminUi.Badge("스크립트 다시 붙여넣기 필요", "Warning") : AdminUi.Badge("아직 연결 안 됨", "Neutral");
        state.Margin = new Thickness(0, 0, 0, 14); body.Children.Add(state);
        var steps = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(steps);
        StackPanel Step(int number, string title, string detail)
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            dot.SetResourceReference(Border.BackgroundProperty, "Pale"); ((TextBlock)dot.Child).SetResourceReference(TextBlock.ForegroundProperty, "Blue"); grid.Children.Add(dot);
            var text = new StackPanel(); Grid.SetColumn(text, 1); text.Children.Add(AdminUi.Heading(title, 15)); var d = AdminUi.Label(detail, "Muted", 13); d.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(d); grid.Children.Add(text);
            var card = AdminUi.Card(grid, 18); card.Margin = new Thickness(0, 0, 0, 10); steps.Children.Add(card); return text;
        }
        Step(1, "수강신청용 구글 계정으로 로그인", "학교 도메인 계정은 ‘모든 사용자’ 공개가 막혀 있는 경우가 많아, 학교 이름으로 개인 구글 계정을 하나 만들어 쓰는 것을 권합니다. 이 계정의 비밀번호는 담당 선생님끼리 인계하세요.");
        var second = Step(2, "새 스크립트에 코드 붙여넣기", "브라우저에서 script.google.com의 새 프로젝트를 열고, 원래 있던 코드를 모두 지운 뒤 아래 ‘코드 복사’로 복사한 코드를 붙여넣고 저장(Ctrl+S)합니다.");
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        var copy = new Button { Content = "코드 복사", Margin = new Thickness(0, 0, 8, 0) }; copy.SetResourceReference(StyleProperty, "PrimaryButton");
        copy.Click += async (_, _) => await Run(async () =>
        {
            var script = await api.Get<System.Text.Json.JsonElement>("api/courses/script");
            try { Clipboard.SetText(script.GetProperty("code").GetString()!); } catch (System.Runtime.InteropServices.ExternalException) { throw new InvalidOperationException("클립보드를 쓰지 못했습니다. 다시 눌러 주세요."); }
            Say("코드를 복사했습니다. 이 코드에는 학교 서버와 구글을 잇는 열쇠가 들어 있으니 다른 곳에 붙여넣지 마세요.", false);
        });
        var browse = new Button { Content = "브라우저에서 새 스크립트 열기" }; AutomationProperties.SetName(browse, "브라우저에서 새 스크립트 열기");
        browse.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("https://script.google.com/home/projects/create") { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { Say("브라우저를 열지 못했습니다. 주소창에 script.google.com 을 직접 입력하세요.", true); } };
        buttons.Children.Add(copy); buttons.Children.Add(browse); second.Children.Add(buttons);
        Step(3, "웹 앱으로 배포", "오른쪽 위 ‘배포 → 새 배포’를 누르고 유형에서 ‘웹 앱’을 고릅니다. ‘다음 사용자 인증 정보로 실행: 나’, ‘액세스 권한이 있는 사용자: 모든 사용자’로 두고 배포합니다. 권한을 묻는 창이 뜨면 계정을 고르고 ‘허용’을 누릅니다(‘확인되지 않은 앱’ 경고가 나오면 ‘고급 → 이동’).");
        var fourth = Step(4, "웹 앱 URL 붙여넣기", "배포가 끝나면 나오는 ‘웹 앱 URL’(…/exec로 끝남)을 복사해 아래에 붙여넣고 ‘연결 확인’을 누릅니다.");
        var line = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var check = new Button { Content = "연결 확인", Margin = new Thickness(8, 0, 0, 0) }; check.SetResourceReference(StyleProperty, "PrimaryButton"); DockPanel.SetDock(check, Dock.Right); line.Children.Add(check);
        var url = new TextBox { Text = setup.ScriptUrl }; AutomationProperties.SetName(url, "웹 앱 URL"); line.Children.Add(url); fourth.Children.Add(line);
        check.Click += async (_, _) => await Run(async () =>
        {
            var result = await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, "api/courses/connect", new { url = url.Text });
            await Connect(); Say(result.GetProperty("scriptCurrent").GetBoolean() ? "연결되었습니다. 이제 ‘신청 · 결과’에서 신청을 만들 수 있습니다." : "연결은 되었지만 붙여넣은 코드가 옛 버전입니다. 코드를 다시 복사해 붙여넣고 ‘배포 → 배포 관리 → 새 버전’으로 다시 배포하세요.", !result.GetProperty("scriptCurrent").GetBoolean());
        }, "구글 스크립트에 연결하는 중…");
        var after = AdminUi.Label("교무톡이 업데이트되어 코드가 바뀌면 ‘스크립트 다시 붙여넣기 필요’가 표시됩니다. 그때는 2번처럼 코드를 다시 붙여넣고 ‘배포 → 배포 관리 → 수정 → 새 버전’으로 배포하면 주소는 그대로입니다.", "Faint", 12);
        after.Margin = new Thickness(0, 6, 0, 0); steps.Children.Add(after);
    }
}
