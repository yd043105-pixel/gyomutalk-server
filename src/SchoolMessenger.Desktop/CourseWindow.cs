using System;
using System.Collections.Generic;
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
public sealed partial class CourseWindow : Window
{
    public sealed record Round(string Id, string Title, int Grade, long Closes, long? PublishedAt, long? PulledAt, int Students, int Submitted);
    public sealed record Student(string Id, int Grade, int ClassNo, int Number, string Name);
    public sealed record Group(string Name, int Pick, string[] Courses, int Semester = 0, string[]? Closed = null, Dictionary<string, string[]>? Kinds = null);
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
    readonly ScrollViewer page = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 8, 24, 12), FontSize = 12 };
    bool busy; string tab = "rounds"; int rosterGrade; int rosterClass;
    static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    static readonly string[] Terms = ["1·2학기", "1학기", "2학기"];
    int overlapTerm = 1; string resultView = "counts";

    public CourseWindow(MainWindow main)
    {
        api = main.Api!; main.RegisterWorkWindow(this); Resources = main.Resources;
        Title = "교무톡 · 수강신청"; Width = 960; Height = 760; MinWidth = 700; MinHeight = 500; FontSize = 13;
        Style = (Style)FindResource("SchoolWindow"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel(); DockPanel.SetDock(status, Dock.Bottom); dock.Children.Add(status);
        page.Content = body; dock.Children.Add(page);
        dock.SetResourceReference(BackgroundProperty, "Surface"); Content = dock;
        Loaded += async (_, _) => await Run(Open);
        Closed += (_, _) => { try { if (Directory.Exists(PrintFolder)) Directory.Delete(PrintFolder, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } };
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
        // Every new page starts at its top, not where the last page was scrolled to.
        body.Children.Clear(); page.ScrollToTop();
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
        // One view at a time, so the table someone came for is on screen without scrolling past the others.
        // Only the part below the view buttons is redrawn, so the page does not jump back to the top.
        var views = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) }; var content = new StackPanel();
        void Paint(Button chip, bool on)
        {
            if (on) { chip.SetResourceReference(BackgroundProperty, "Pale"); chip.SetResourceReference(ForegroundProperty, "Blue"); chip.SetResourceReference(BorderBrushProperty, "Blue"); }
            else { chip.ClearValue(BackgroundProperty); chip.ClearValue(ForegroundProperty); chip.ClearValue(BorderBrushProperty); }
        }
        var redo = results.Students.Where(s => Lost(round, s).Length > 0).ToArray();
        var choices = new System.Collections.Generic.List<(string Key, string Label)> { ("counts", $"과목별 인원 · 제출 {answered} / {total}명"), ("overlap", "함께 듣는 학생 수"), ("waiting", $"미제출 {total - answered}명") };
        if (redo.Length > 0) choices.Add(("redo", $"폐강 재선택 {redo.Length}명"));
        choices.Add(("sections", "분반 편성")); choices.Add(("print", "확인서 인쇄"));
        if (resultView == "redo" && redo.Length == 0) resultView = "counts";
        foreach (var (key, label) in choices)
        {
            var chip = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), Tag = key }; chip.SetResourceReference(StyleProperty, "FilterButton"); Paint(chip, key == resultView);
            chip.Click += (_, _) => { resultView = key; foreach (var other in views.Children.OfType<Button>()) Paint(other, (string)other.Tag == key); Fill(); }; views.Children.Add(chip);
        }
        body.Children.Add(views); body.Children.Add(content); Fill();
        void Fill()
        {
            content.Children.Clear();
            if (resultView == "overlap")
            {
                var box = new StackPanel();
                if (answered > 0) Overlap(results, box); else box.Children.Add(AdminUi.Label("제출한 학생이 생기면 여기에 표가 나옵니다.", "Faint", 13));
                content.Children.Add(AdminUi.Card(box, 18)); return;
            }
            if (resultView == "waiting")
            {
                var waiting = results.Students.Where(s => s.Picks is null).ToArray();
                var list = new StackPanel(); list.Children.Add(Titled(waiting.Length == 0 ? "모두 제출했습니다" : $"미제출 학생 {waiting.Length}명", waiting.Length == 0 ? null : Plain("미제출 학생 링크만 저장", () => SaveLinks(round, waiting), this)));
                ByClass(list, waiting, s => s.Linked ? "" : "(링크 없음)");
                content.Children.Add(AdminUi.Card(list, 18)); return;
            }
            if (resultView == "redo")
            {
                var list = new StackPanel(); list.Children.Add(Titled($"폐강으로 다시 골라야 하는 학생 {redo.Length}명", Plain("이 학생들 링크만 저장", () => SaveLinks(round, redo), this)));
                var hint = AdminUi.Label("이 학생들이 자기 링크를 다시 열면 폐강 안내가 나오고, 그 선택군만 다시 고르면 됩니다. 마감일이 지났으면 ‘수정’에서 마감일을 늘리세요.", "Muted", 12); hint.Margin = new Thickness(0, 4, 0, 0); list.Children.Add(hint);
                ByClass(list, redo, s => $"({string.Join(", ", Lost(round, s))})");
                content.Children.Add(AdminUi.Card(list, 18)); return;
            }
            if (resultView == "sections") { var box = new StackPanel(); SectionsView(results, box); content.Children.Add(AdminUi.Card(box, 18)); return; }
            if (resultView == "print") { var box = new StackPanel(); Slips(results, box); content.Children.Add(AdminUi.Card(box, 18)); return; }
            foreach (var (group, index) in results.Groups.Select((g, i) => (g, i)))
            {
                var term = round.Groups[index].Semester;
                var stack = new StackPanel(); var name = AdminUi.Heading(term == 0 ? group.Name : $"{group.Name} · {Terms[term]}", 15); stack.Children.Add(name);
                var shut = round.Groups[index].Closed ?? [];
                var hint = AdminUi.Label($"{group.Courses.Length}과목 중 {group.Pick}과목 선택" + (shut.Length > 0 ? $" · 폐강 {string.Join(", ", shut)}" : ""), "Muted", 12); hint.Margin = new Thickness(0, 2, 0, 10); stack.Children.Add(hint);
                // Bars are out of the students who answered, so a long bar means most of the grade chose it.
                var most = Math.Max(1, answered);
                foreach (var course in group.Courses.OrderByDescending(c => c.Count))
                {
                    var line = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); line.ColumnDefinitions.Add(new ColumnDefinition()); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) }); line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                    var closed = shut.Contains(course.Name);
                    var label = AdminUi.Label(closed ? course.Name + " · 폐강" : course.Name, closed ? "Danger" : "Ink", 13); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap; label.ToolTip = round.Groups[index].Kinds?.GetValueOrDefault(course.Name) is { } kind ? $"{course.Name} · {string.Join(" · ", kind.Where(k => k.Length > 0))}" : course.Name; label.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(label);
                    var track = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) }; Grid.SetColumn(track, 1);
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(course.Count, GridUnitType.Star) }); track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(most - course.Count, GridUnitType.Star) });
                    var back2 = new Border { CornerRadius = new CornerRadius(5) }; back2.SetResourceReference(Border.BackgroundProperty, "Subtle"); Grid.SetColumnSpan(back2, 2); track.Children.Add(back2);
                    var fill = new Border { CornerRadius = new CornerRadius(5) }; fill.SetResourceReference(Border.BackgroundProperty, closed ? "Faint" : "Blue"); if (course.Count > 0) track.Children.Add(fill);
                    line.Children.Add(track);
                    var count = AdminUi.Label(course.Count + "명", "Ink", 13, true); count.TextAlignment = TextAlignment.Right; count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 2); line.Children.Add(count);
                    var (groupIndex, courseName, people) = (index, course.Name, course.Count);
                    var toggle = new Button { Content = closed ? "폐강 취소" : "폐강", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 2, 8, 2) };
                    toggle.SetResourceReference(StyleProperty, "SubtleButton"); if (!closed) toggle.SetResourceReference(ForegroundProperty, "Muted");
                    AutomationProperties.SetName(toggle, (closed ? "폐강 취소 " : "폐강 ") + courseName); Grid.SetColumn(toggle, 3); line.Children.Add(toggle);
                    toggle.Click += async (_, _) => await Run(async () =>
                    {
                        if (!closed && Notice.Show(this, $"‘{courseName}’을(를) 폐강할까요?\n\n이 과목을 고른 학생 {people}명은 자기 링크를 다시 열면 이 선택군을 다시 골라야 합니다. 학생 화면에 바로 반영됩니다.\n마감일이 지났다면 ‘수정’에서 마감일을 늘린 뒤 ‘폐강 재선택’의 학생들에게 링크를 다시 보내세요.", "폐강", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                        await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, $"api/courses/rounds/{round.Id}/closed", new { group = groupIndex, course = courseName, closed = !closed });
                        await ShowRound(round.Id, false); Say(closed ? $"‘{courseName}’ 폐강을 취소했습니다. 학생 화면에 다시 나옵니다." : $"‘{courseName}’을(를) 폐강했습니다. ‘폐강 재선택’에서 다시 골라야 하는 학생을 볼 수 있습니다.", false);
                    }, round.PublishedAt is null ? null : "학생 화면에 반영하는 중…");
                    stack.Children.Add(line);
                }
                var card = AdminUi.Card(stack, 18); card.Margin = new Thickness(0, 0, 0, 12); content.Children.Add(card);
            }
        }
    }
    // The closed courses a student had chosen; they choose again in those groups.
    static string[] Lost(Detail round, Answer s) => s.Picks is not { } p ? [] : round.Groups.SelectMany((g, i) => i < p.Length ? p[i].Where(c => (g.Closed ?? []).Contains(c)) : Enumerable.Empty<string>()).ToArray();
    static DockPanel Titled(string title, Button? action)
    {
        var head = new DockPanel();
        if (action is not null) { action.Margin = new Thickness(8, 0, 0, 0); action.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(action, Dock.Right); head.Children.Add(action); }
        head.Children.Add(AdminUi.Heading(title, 15)); return head;
    }
    static void ByClass(Panel host, System.Collections.Generic.IEnumerable<Answer> students, Func<Answer, string>? note = null)
    {
        foreach (var room in students.GroupBy(s => s.ClassNo).OrderBy(r => r.Key))
        {
            var line = new Grid { Margin = new Thickness(0, 10, 0, 0) }; line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) }); line.ColumnDefinitions.Add(new ColumnDefinition());
            line.Children.Add(AdminUi.Label($"{room.Key}반 {room.Count()}명", "Muted", 13, true));
            var names = AdminUi.Label(string.Join("   ", room.OrderBy(s => s.Number).Select(s => $"{s.Number} {s.Name}" + (note?.Invoke(s) ?? ""))), "Ink", 13); Grid.SetColumn(names, 1); line.Children.Add(names);
            host.Children.Add(line);
        }
    }

    // ── Courses taken together in one term: the cell where two courses meet counts the students who chose both,
    // which is what decides the timetable blocks. A number opens the list of those students. ──
    void Overlap(Results results, StackPanel box)
    {
        box.Children.Clear();
        var groups = results.Round.Groups; var split = groups.Any(g => g.Semester != 0); var term = split ? overlapTerm : 0;
        var inTerm = groups.Select((g, i) => (Group: g, Index: i)).Where(x => term == 0 || x.Group.Semester == 0 || x.Group.Semester == term).ToArray();
        // Closed courses are left out: no class will run for them.
        var courses = inTerm.SelectMany(x => x.Group.Courses.Except(x.Group.Closed ?? [])).Distinct().ToArray();
        var takers = results.Students.Where(s => s.Picks is not null).Select(s => (Student: s, Courses: inTerm.SelectMany(x => s.Picks![x.Index].Except(x.Group.Closed ?? [])).ToHashSet())).ToArray();
        int Both(int a, int b) => takers.Count(t => t.Courses.Contains(courses[a]) && t.Courses.Contains(courses[b]));
        var counts = new int[courses.Length, courses.Length];
        for (var a = 0; a < courses.Length; a++) for (var b = 0; b < courses.Length; b++) counts[a, b] = Both(a, b);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var save = new Button { Content = "표 저장", VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(save, Dock.Right); head.Children.Add(save);
        head.Children.Add(AdminUi.Heading(split ? $"함께 듣는 학생 수 · {Terms[term]}" : "함께 듣는 학생 수", 15)); box.Children.Add(head);
        var hint = AdminUi.Label("두 과목이 만나는 칸은 두 과목을 모두 고른 학생 수, 대각선의 숫자는 그 과목을 고른 학생 수입니다.", "Muted", 12);
        hint.Margin = new Thickness(0, 0, 0, 10); box.Children.Add(hint);
        if (split)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var t in new[] { 1, 2 })
            {
                var chip = new Button { Content = Terms[t], Margin = new Thickness(0, 0, 6, 6) }; chip.SetResourceReference(StyleProperty, "FilterButton");
                if (t == term) { chip.SetResourceReference(BackgroundProperty, "Pale"); chip.SetResourceReference(ForegroundProperty, "Blue"); chip.SetResourceReference(BorderBrushProperty, "Blue"); }
                chip.Click += (_, _) => { overlapTerm = t; Overlap(results, box); }; chips.Children.Add(chip);
            }
            box.Children.Add(chips);
        }
        if (courses.Length == 0) { box.Children.Add(AdminUi.Label($"{Terms[term]}에 듣는 선택군이 없습니다.", "Faint", 13)); return; }

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Clamp(courses.Max(c => c.Length) * 13 + 16, 44, 124)) });
        foreach (var _ in courses) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); }
        var most = Math.Max(1, Enumerable.Range(0, courses.Length).SelectMany(a => Enumerable.Range(0, courses.Length).Where(b => b != a).Select(b => counts[a, b])).DefaultIfEmpty(0).Max());
        var chosen = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 0, 8) };
        Button? lit = null;
        for (var i = 0; i < courses.Length; i++)
        {
            // Column names stand on their side so a dozen courses still fit across the window. A fixed size keeps the turned text from being squeezed to the column width.
            var top = new TextBlock { Text = courses[i], FontSize = 12, ToolTip = courses[i], TextTrimming = TextTrimming.CharacterEllipsis, Width = 108, Margin = new Thickness(0, 0, 0, 6),
                LayoutTransform = new RotateTransform(-90), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            top.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Grid.SetColumn(top, i + 1); grid.Children.Add(top);
            var side = AdminUi.Label(courses[i], "Ink", 13); side.TextTrimming = TextTrimming.CharacterEllipsis; side.TextWrapping = TextWrapping.NoWrap; side.ToolTip = courses[i];
            side.VerticalAlignment = VerticalAlignment.Center; side.Margin = new Thickness(0, 0, 8, 0); Grid.SetRow(side, i + 1); grid.Children.Add(side);
            for (var j = 0; j < courses.Length; j++)
            {
                int a = i, b = j, n = counts[i, j];
                var cell = new Button { Content = n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture), Margin = new Thickness(1), Padding = new Thickness(0), MinHeight = 0, MinWidth = 0, BorderThickness = new Thickness(1), IsEnabled = n > 0 };
                cell.SetResourceReference(StyleProperty, "SubtleButton");
                if (a == b) { cell.SetResourceReference(BackgroundProperty, "Pale"); cell.FontWeight = FontWeights.Bold; }
                else cell.Background = new SolidColorBrush(Color.FromArgb((byte)(n == 0 ? 0 : 24 + 120 * n / most), 0x24, 0x63, 0xEB));
                cell.SetResourceReference(ForegroundProperty, "Ink"); cell.BorderBrush = Brushes.Transparent;
                AutomationProperties.SetName(cell, a == b ? $"{courses[a]} 고른 학생" : $"{courses[a]} + {courses[b]} 함께 듣는 학생");
                cell.ToolTip = a == b ? $"{courses[a]} {n}명" : $"{courses[a]} + {courses[b]} {n}명";
                cell.Click += (_, _) =>
                {
                    if (lit is not null) lit.BorderBrush = Brushes.Transparent; lit = cell; cell.SetResourceReference(BorderBrushProperty, "Blue");
                    chosen.Children.Clear();
                    var title = AdminUi.Heading(a == b ? $"{courses[a]} 고른 학생 {n}명" : $"{courses[a]} + {courses[b]} 함께 듣는 학생 {n}명", 14); title.TextWrapping = TextWrapping.Wrap; chosen.Children.Add(title);
                    ByClass(chosen, takers.Where(t => t.Courses.Contains(courses[a]) && t.Courses.Contains(courses[b])).Select(t => t.Student));
                };
                Grid.SetRow(cell, i + 1); Grid.SetColumn(cell, j + 1); grid.Children.Add(cell);
            }
        }
        var scroller = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        // The page scrolls up and down; the wheel over the table should move the page, not stop at the table.
        scroller.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            ((UIElement)scroller.Parent).RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = scroller });
        };
        // The students sit beside the table when there is room, and drop below it in a narrow window.
        scroller.Margin = new Thickness(0, 0, 24, 8);
        var pair = new WrapPanel(); pair.Children.Add(scroller); pair.Children.Add(chosen); box.Children.Add(pair);
        chosen.Children.Add(AdminUi.Label("표의 숫자를 누르면 해당 학생의 반·번호·이름이 여기에 나옵니다.", "Faint", 12));
        save.Click += (_, _) => Save($"{results.Round.Title} 함께 듣는 학생 수" + (split ? $" {Terms[term]}" : "") + ".csv", "과목," + string.Join(",", courses.Select(Cell)),
            Enumerable.Range(0, courses.Length).Select(a => new object[] { courses[a] }.Concat(Enumerable.Range(0, courses.Length).Select(b => (object)counts[a, b])).ToArray()), "표를 저장했습니다.");
    }

    async Task SaveLinks(Detail round, Answer[]? only = null)
    {
        var links = await api.Get<StudentLink[]>($"api/courses/rounds/{round.Id}/links");
        if (only is not null) links = links.Where(l => only.Any(s => s.ClassNo == l.ClassNo && s.Number == l.Number)).ToArray();
        Save($"{round.Title} 학생 링크{(only is null ? "" : $" {links.Length}명")}.csv", "학년,반,번호,이름,링크", links.Select(l => new object[] { l.Grade, l.ClassNo, l.Number, l.Name, l.Link }),
            "링크 목록을 저장했습니다. 학생 한 명마다 자기 링크만 받도록 나눠 주세요.");
    }
    Task SaveResults(Results results)
    {
        var groups = results.Round.Groups;
        Save($"{results.Round.Title} 결과.csv", "학년,반,번호,이름,제출," + string.Join(",", groups.Select(g => Cell(g.Name))) + ",제출 시각",
            results.Students.Select(s => new object[] { results.Round.Grade, s.ClassNo, s.Number, s.Name, s.Picks is null ? "미제출" : "제출" }
                .Concat(groups.Select((g, i) => (object)(s.Picks is null ? "" : string.Join(", ", s.Picks[i].Select(c => (g.Closed ?? []).Contains(c) ? c + "(폐강)" : c))))).Append(s.SubmittedAt is { } at ? When(at) : "").ToArray()), "결과를 저장했습니다.");
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

    // ── 수강신청 확인서: one A4 page per student, by class, opened in the browser to print or save as PDF ──
    bool blankSlips = true;
    static readonly string PrintFolder = Path.Combine(Path.GetTempPath(), "gyomutalk-print");
    void Slips(Results results, StackPanel box)
    {
        box.Children.Add(AdminUi.Heading("수강신청 확인서", 15));
        var hint = AdminUi.Label("학생 한 명에 A4 한 장입니다. 학생·학부모 서명 칸과 신청 일자를 적는 칸이 있습니다. 반을 누르면 브라우저에 열리니 ‘인쇄하기’를 누르세요(PDF로 저장도 됩니다).", "Muted", 12);
        hint.Margin = new Thickness(0, 4, 0, 10); box.Children.Add(hint);
        var blanks = new CheckBox { Content = "미제출 학생은 빈 신청서로 함께 뽑기", IsChecked = blankSlips, Margin = new Thickness(0, 0, 0, 12) };
        blanks.Click += (_, _) => blankSlips = blanks.IsChecked == true; box.Children.Add(blanks);
        var rooms = new WrapPanel();
        var all = new Button { Content = "전체 반", Margin = new Thickness(0, 0, 8, 8) }; all.SetResourceReference(StyleProperty, "PrimaryButton");
        all.Click += async (_, _) => await Run(() => OpenSlips(results, results.Students, "전체"), "확인서를 만드는 중…"); rooms.Children.Add(all);
        foreach (var room in results.Students.GroupBy(s => s.ClassNo).OrderBy(r => r.Key))
        {
            var students = room.ToArray();
            var open = new Button { Content = $"{room.Key}반 · 제출 {students.Count(s => s.Picks is not null)}/{students.Length}", Margin = new Thickness(0, 0, 8, 8) };
            open.Click += async (_, _) => await Run(() => OpenSlips(results, students, $"{room.Key}반"), "확인서를 만드는 중…"); rooms.Children.Add(open);
        }
        box.Children.Add(rooms);
        var note = AdminUi.Label("인쇄용 파일은 이 PC의 임시 폴더에만 잠깐 만들어지고, 다음 인쇄나 수강신청 창을 닫을 때 지워집니다.", "Faint", 12); note.Margin = new Thickness(0, 6, 0, 0); box.Children.Add(note);
    }
    async Task OpenSlips(Results results, Answer[] students, string label)
    {
        var round = results.Round; var chosen = students.Where(s => blankSlips || s.Picks is not null).OrderBy(s => s.ClassNo).ThenBy(s => s.Number).ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("뽑을 학생이 없습니다. 미제출 학생도 뽑으려면 ‘빈 신청서로 함께 뽑기’를 켜세요.");
        var school = "학교";
        try { school = (await api.Get<System.Text.Json.JsonElement>("server-info")).GetProperty("schoolName").GetString() is { Length: > 0 } name ? name : school; } catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or KeyNotFoundException) { }
        string H(string text) => System.Net.WebUtility.HtmlEncode(text);
        var pages = new StringBuilder();
        foreach (var s in chosen)
        {
            var rows = string.Concat(round.Groups.Select((g, i) =>
            {
                var picked = s.Picks is { } p && i < p.Length ? string.Join(", ", p[i].Select(c => (g.Closed ?? []).Contains(c) ? H(c) + " <small>(폐강 · 다시 신청)</small>" : H(c))) : "";
                return $"<tr><th>{H(g.Name)}</th><td class=\"term\">{(g.Semester == 0 ? "1·2학기" : g.Semester + "학기")}</td><td class=\"{(picked.Length == 0 ? "blank" : "")}\">{(picked.Length == 0 ? $"<small>{g.Pick}과목을 적으세요 · {H(string.Join(", ", g.Courses.Except(g.Closed ?? [])))}</small>" : picked)}</td></tr>";
            }));
            var stamp = s.SubmittedAt is { } at ? $"온라인 제출 {DateTimeOffset.FromUnixTimeMilliseconds(at).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy. M. d. HH:mm", Korean)}" : "온라인 미제출";
            pages.Append($@"<section class=""page""><h1>수강신청 확인서</h1><p class=""round"">{H(round.Title)}</p>
<table class=""who""><tr><th>학년</th><td>{round.Grade}</td><th>반</th><td>{s.ClassNo}</td><th>번호</th><td>{s.Number}</td><th>이름</th><td class=""name"">{H(s.Name)}</td></tr></table>
<table class=""picks""><tr><th>선택군</th><th class=""term"">학기</th><th>신청 과목</th></tr>{rows}</table><p class=""meta"">{stamp}</p>
<p class=""vow"">위와 같이 선택과목 수강을 신청하였음을 확인합니다.</p>
<p class=""date"">신청 일자 :&emsp;&emsp;&emsp;&emsp;년&emsp;&emsp;&emsp;월&emsp;&emsp;&emsp;일</p>
<div class=""sign""><span>학생 :</span><b>{H(s.Name)}</b><span>(인)</span><span>학부모(보호자) :</span><b></b><span>(인)</span></div>
<p class=""to"">{H(school)}장 귀하</p></section>");
        }
        var html = $@"<!doctype html><html lang=""ko""><head><meta charset=""utf-8""><title>{H(round.Title)} 수강신청 확인서 {H(label)}</title><style>
@page{{size:A4;margin:15mm}}
body{{margin:0;background:#E9EDF3;color:#111;font-family:""Malgun Gothic"",""Apple SD Gothic Neo"",sans-serif}}
.bar{{position:sticky;top:0;z-index:1;display:flex;gap:14px;align-items:center;background:#fff;border-bottom:1px solid #DDE3EA;padding:10px 18px;font-size:14px;color:#5B6B80}}
.bar b{{color:#17233A}}.bar button{{font:inherit;font-weight:700;background:#2463EB;color:#fff;border:0;border-radius:8px;padding:8px 18px;cursor:pointer}}
.page{{box-sizing:border-box;width:210mm;height:297mm;margin:16px auto;padding:18mm 17mm;background:#fff;box-shadow:0 1px 4px rgba(0,0,0,.12);display:flex;flex-direction:column;page-break-after:always;break-after:page}}
h1{{margin:0;text-align:center;font-size:24pt;letter-spacing:.35em}}.round{{margin:3mm 0 9mm;text-align:center;font-size:12pt;color:#333}}
table{{width:100%;border-collapse:collapse;font-size:11.5pt}}th,td{{border:1px solid #444;padding:3mm}}th{{background:#F1F3F6;font-weight:600;white-space:nowrap}}
.who td,.who th{{text-align:center}}.who .name{{font-weight:700;min-width:28mm}}.picks{{margin-top:6mm}}.picks th{{width:27%}}.picks .term{{width:13%;text-align:center}}
.picks td.blank{{height:21mm;vertical-align:top}}small{{color:#666;font-size:9pt}}.meta{{margin:2mm 0 0;text-align:right;font-size:9pt;color:#666}}
.vow{{margin:12mm 0 7mm;text-align:center;font-size:12.5pt}}.date{{margin:0 0 11mm;text-align:center;font-size:12.5pt}}
.sign{{display:grid;grid-template-columns:auto 42mm auto;column-gap:4mm;row-gap:9mm;align-items:end;margin:4mm 0 0 auto;font-size:12.5pt}}.sign span:nth-child(3n+1){{text-align:right}}.sign b{{font-weight:600;text-align:center}}.sign span:nth-child(3n){{color:#555}}.to{{margin:auto 0 0;padding-top:10mm;text-align:center;font-size:16pt;font-weight:700}}
@media print{{body{{background:#fff}}.bar{{display:none}}.page{{margin:0;width:auto;height:266mm;padding:0;box-shadow:none}}}}
</style></head><body><div class=""bar""><button onclick=""print()"">인쇄하기</button><span><b>{H(label)} {chosen.Length}명</b> · 학생 한 명에 한 장 · PDF로 저장하려면 인쇄 창에서 ‘PDF로 저장’을 고르세요.</span></div>
{pages}</body></html>";
        // Only the latest printout is kept, and only until this window closes, since it holds students' names.
        Directory.CreateDirectory(PrintFolder); foreach (var old in Directory.GetFiles(PrintFolder, "*.html")) { try { File.Delete(old); } catch (IOException) { } }
        var file = Path.Combine(PrintFolder, $"확인서 {Guid.NewGuid():N}.html"); File.WriteAllText(file, html, new UTF8Encoding(false));
        // The screen tour keeps the page with its pictures instead of opening a browser on the test machine.
        if (Environment.GetEnvironmentVariable("YY_SCREEN_TOUR") is { Length: > 0 } tour) { File.Copy(file, Path.Combine(tour, $"확인서 {label}.html"), true); Say($"{label} 확인서 {chosen.Length}장을 만들었습니다.", false); return; }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("브라우저를 열지 못했습니다. 기본 브라우저가 정해져 있는지 확인하세요."); }
        Say($"{label} 확인서 {chosen.Length}장을 브라우저에 열었습니다. ‘인쇄하기’를 누르세요.", false);
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
            var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBox { Text = g?.Name ?? $"선택군 {(char)('A' + groups.Children.Count)}", MaxLength = 40, ToolTip = "선택군 이름" }; AutomationProperties.SetName(name, "선택군 이름"); top.Children.Add(name);
            var term = new ComboBox { ItemsSource = Terms, SelectedIndex = g?.Semester ?? 0, ToolTip = "이 선택군 과목을 듣는 학기. 결과의 ‘함께 듣는 학생 수’를 학기별로 나눠 봅니다." }; AutomationProperties.SetName(term, "듣는 학기"); Grid.SetColumn(term, 2); top.Children.Add(term);
            var pick = new ComboBox { ItemsSource = Enumerable.Range(1, 10).Select(n => n + "과목 고르기").ToArray(), SelectedIndex = (g?.Pick ?? 1) - 1 }; AutomationProperties.SetName(pick, "고를 과목 수"); Grid.SetColumn(pick, 4); top.Children.Add(pick);
            var drop = new Button { Content = "빼기", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(drop, 5); top.Children.Add(drop); AutomationProperties.SetName(drop, "선택군 빼기");
            card.Children.Add(top);
            var head = CourseRows.Columns(); head.Margin = new Thickness(0, 12, 0, 4);
            foreach (var (text, col) in new[] { ("과목 (이름을 적거나 ▾에서 고르기)", 0), ("교과(군)", 2), ("유형", 4) }) { var c = AdminUi.Label(text, "Faint", 12); Grid.SetColumn(c, col); head.Children.Add(c); }
            var rows = new CourseRows(); card.Children.Add(head); card.Children.Add(rows);
            foreach (var c in g?.Courses ?? []) rows.Add(c, g?.Kinds?.GetValueOrDefault(c));
            if (rows.Children.Count == 0) { rows.Add(""); rows.Add(""); }
            var addCourse = new Button { Content = "+ 과목 추가", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
            addCourse.Click += (_, _) => rows.Add("", focus: true); card.Children.Add(addCourse);
            var shell = AdminUi.Card(card, 16); shell.Margin = new Thickness(0, 0, 0, 10); shell.Tag = (name, pick, rows, term); groups.Children.Add(shell);
            drop.Click += (_, _) => { if (groups.Children.Count > 1) groups.Children.Remove(shell); };
        }
        foreach (var g in round?.Groups ?? [new Group("선택군 A", 1, [])]) AddGroup(g);
        var more = new Button { Content = "+ 선택군 추가", HorizontalAlignment = HorizontalAlignment.Left }; more.Click += (_, _) => AddGroup(null); form.Children.Add(more);
        if (round?.PublishedAt is not null) { var note = AdminUi.Label("저장하면 바뀐 내용이 학생 화면에 바로 반영됩니다. 제출한 학생이 있으면 학년·선택군·과목은 바꿀 수 없고, 신청 이름·마감일·학기만 바꿀 수 있습니다.", "Muted", 12); note.Margin = new Thickness(0, 14, 0, 0); form.Children.Add(note); }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
        var save = new Button { Content = "저장" }; save.SetResourceReference(StyleProperty, "PrimaryButton");
        var cancel = new Button { Content = "취소", Margin = new Thickness(8, 0, 0, 0) };
        actions.Children.Add(save); actions.Children.Add(cancel); form.Children.Add(actions);
        cancel.Click += async (_, _) => await Run(() => round is null ? Rounds() : ShowRound(round.Id, false));
        save.Click += async (_, _) => await Run(async () =>
        {
            if (closes.SelectedDate is not { } day) throw new InvalidOperationException("마감일을 고르세요.");
            var list = groups.Children.OfType<Border>().Select(b => ((TextBox Name, ComboBox Pick, CourseRows Rows, ComboBox Term))b.Tag).Select(t =>
            {
                var items = t.Rows.Items;
                var kinds = items.Where(r => r.Kind.Any(k => k.Length > 0)).DistinctBy(r => r.Name).ToDictionary(r => r.Name, r => r.Kind);
                return new Group(t.Name.Text, t.Pick.SelectedIndex + 1, items.Select(r => r.Name).ToArray(), t.Term.SelectedIndex, Kinds: kinds);
            }).ToArray();
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
                await api.Send<System.Text.Json.JsonElement>(HttpMethod.Post, $"api/courses/students/{s.Id}/delete"); await Roster(); Say(s.Name + " 학생을 뺐습니다. 이미 보낸 신청에서도 이 학생 링크와 제출 내용을 지웠습니다.", false);
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
            rosterGrade = Number(grade); rosterClass = Number(room); await Roster(); Say(name.Text.Trim() + " 학생을 더했습니다. 이미 링크를 보낸 신청이 있으면 이 학생 링크도 만들었으니 ‘링크 목록 저장’으로 받아 전해 주세요.", false);
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
        Say($"명단을 넣었습니다. 새로 들어온 학생 {done.GetProperty("added").GetInt32()}명, 빠진 학생 {done.GetProperty("removed").GetInt32()}명. 이미 보낸 신청에도 바로 반영했습니다.", false);
    }

    // The step-by-step Google guide (with pictures) ships inside the program and opens in the browser beside the script editor.
    static void OpenGuide()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gyomutalk-guide"); Directory.CreateDirectory(dir);
        var assembly = typeof(CourseWindow).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Guide/", StringComparison.Ordinal)))
        {
            using var from = assembly.GetManifestResourceStream(name)!; using var to = File.Create(Path.Combine(dir, name["Guide/".Length..])); from.CopyTo(to);
        }
        Process.Start(new ProcessStartInfo(Path.Combine(dir, "course-google.html")) { UseShellExecute = true });
    }

    // ── Google connection: paste the generated script once, deploy it, paste its address back ──
    async Task Connect()
    {
        var setup = await api.Get<Setup>("api/courses/setup");
        Header();
        var state = setup.Connected ? setup.ScriptCurrent ? AdminUi.Badge("연결됨", "Success") : AdminUi.Badge("스크립트 다시 붙여넣기 필요", "Warning") : AdminUi.Badge("아직 연결 안 됨", "Neutral");
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) }; state.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(state);
        var guide = new Button { Content = "그림으로 보는 자세한 안내", Margin = new Thickness(12, 0, 0, 0) }; guide.SetResourceReference(StyleProperty, "SubtleButton");
        guide.Click += (_, _) => { try { OpenGuide(); Say("브라우저에 안내를 열었습니다. 안내를 보면서 한 단계씩 따라 하세요.", false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { Say("안내를 열지 못했습니다. 다시 눌러 보고, 그래도 안 되면 기본 브라우저가 정해져 있는지 확인하세요.", true); } };
        top.Children.Add(guide); body.Children.Add(top);
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
        Step(3, "웹 앱으로 배포", "오른쪽 위 ‘배포 → 새 배포’에서 톱니바퀴를 눌러 ‘웹 앱’을 고릅니다. ‘액세스 권한이 있는 사용자’를 ‘모든 사용자’로 바꾸고 배포합니다. ‘액세스 승인’을 눌러 계정을 고르고 허용합니다(‘Google에서 확인하지 않은 앱’ 경고가 나오면 ‘고급 → 이동’).");
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
