using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

// 교체 도우미: the teacher says which periods to empty; the server finds whole requests that keep every rule, same-day trades
// first. When the teacher writes a wish and the school allows AI, the AI puts those requests in the order that suits it.
// The chosen one lands on the board as ordinary changes, to be checked and sent as usual.
public sealed partial class TimetableWindow
{
    sealed record HelperParts(Window Window, DatePicker Day, CheckBox[] Periods, TextBox Wish, TextBlock Note, StackPanel Results, Func<Task> Find);

    Task SwapHelper() { BuildHelper().Window.ShowDialog(); return Task.CompletedTask; }

    HelperParts BuildHelper()
    {
        var window = new Window { Owner = this, Title = "교체 도우미", Width = 580, Height = Math.Min(720, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource("SchoolWindow") };
        window.SetResourceReference(BackgroundProperty, "Soft");
        var page = new DockPanel { Margin = new Thickness(22, 18, 22, 18) }; window.Content = page;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); page.Children.Add(top);
        top.Children.Add(AdminUi.PageTitle("교체 도우미", "비울 시간을 고르면 겹침·수업 불가 시간·지난 수업을 지키는 교체안을 찾습니다. 같은 날 교체를 먼저, 다른 주 교체는 나중에 보여 줍니다."));

        var form = new StackPanel(); top.Children.Add(AdminUi.Card(form, 16));
        form.Children.Add(AdminUi.Label("비울 날짜와 교시", "Ink", 13, true));
        var today = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var first = BoardDates.FirstOrDefault(d => string.CompareOrdinal(d, today) >= 0) ?? BoardDates[0];
        var day = new DatePicker { SelectedDate = TimetableDate(first), Width = 140, Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(day, "비울 날짜");
        var row = new WrapPanel(); row.Children.Add(day);
        var periods = Enumerable.Range(1, 7).Select(n => new CheckBox { Content = n + "교시", Margin = new Thickness(0, 8, 12, 0), VerticalAlignment = VerticalAlignment.Center }).ToArray();
        foreach (var box in periods) row.Children.Add(box);
        form.Children.Add(row);
        var wishLabel = AdminUi.Label("AI에게 전할 말 (선택)", "Ink", 13, true); wishLabel.Margin = new Thickness(0, 14, 0, 6); form.Children.Add(wishLabel);
        var wish = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 50, MaxLength = 300 };
        AutomationProperties.SetName(wish, "AI에게 전할 말");
        var wishHint = AdminUi.Label("예: 오후 수업과는 바꾸지 말아 주세요. 3학년 수업은 피해 주세요.", "Faint", 12); wishHint.Margin = new Thickness(8, 6, 0, 0); wishHint.IsHitTestVisible = false; wishHint.VerticalAlignment = VerticalAlignment.Top;
        wish.TextChanged += (_, _) => wishHint.Visibility = wish.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var wishField = new Grid(); wishField.Children.Add(wish); wishField.Children.Add(wishHint); form.Children.Add(wishField);
        var aiNote = AdminUi.Label("적으면 AI가 찾은 교체안 중 맞는 순서를 고릅니다. 요청 글과 요일·교시·학급·과목만 보내고 선생님 이름은 기호로 바꿉니다.", "Faint", 11);
        aiNote.TextWrapping = TextWrapping.Wrap; aiNote.Margin = new Thickness(0, 6, 0, 0); form.Children.Add(aiNote);
        var mark = new CheckBox { Content = "비운 시간을 내 ‘수업 불가’ 시간으로 함께 표시", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
        form.Children.Add(mark);
        var find = new Button { Content = "교체안 찾기", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 120, Margin = new Thickness(0, 12, 0, 0) };
        find.SetResourceReference(StyleProperty, "PrimaryButton"); form.Children.Add(find);
        var note = AdminUi.Label("", "Muted", 12); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 0); top.Children.Add(note);
        var results = new StackPanel();
        page.Children.Add(new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) });

        async Task Find()
        {
            results.Children.Clear(); note.Text = "";
            if (day.SelectedDate is not { } chosen) throw new InvalidOperationException("비울 날짜를 고르세요.");
            var hours = periods.Select((box, i) => (box, i)).Where(x => x.box.IsChecked == true).Select(x => x.i + 1).ToArray();
            if (hours.Length == 0) throw new InvalidOperationException("비울 교시를 하나 이상 고르세요.");
            var date = chosen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var monday = chosen.Date.AddDays(-(((int)chosen.DayOfWeek + 6) % 7));
            var dates = Enumerable.Range(0, 5).Select(i => monday.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
            if (!dates.Contains(date)) throw new InvalidOperationException("월요일부터 금요일 사이의 수업일을 고르세요.");
            var vacate = hours.Select(h => new TimetableTime(date, h)).ToArray();
            var what = DayLabel(date) + " " + string.Join(", ", hours.Select(h => h + "교시"));
            find.IsEnabled = false; find.Content = "찾는 중…";
            try
            {
                var found = await api.Send<TimetableSuggestion[]>(HttpMethod.Post, "api/timetable/swap-suggestions", new TimetableSuggestionRequest(dates, [], null, vacate));
                var plan = await api.Send<TimetableSwapPlan>(HttpMethod.Post, "api/timetable/swap-plan", new TimetableSwapPlanRequest(dates, [], null));
                var lessons = plan.Slots.ToDictionary(s => s.Position, s => s.Lesson);
                if (found.Length == 0) { note.Text = what + "을(를) 비울 교체안이 교체할 수 있는 범위 안에 없습니다. 교시를 줄이거나, 표에서 직접 옮기거나, 관련 선생님과 상의해 보세요."; return; }
                // Ideas may reach into the weeks lessons trade across; their lessons are read a week at a time.
                foreach (var week in found.SelectMany(f => f.Exchanges).SelectMany(e => new[] { e.First.Date, e.Second.Date }).Where(d => !dates.Contains(d))
                    .Select(d => { var t = DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture); return t.AddDays(-(((int)t.DayOfWeek + 6) % 7)); }).Distinct())
                    foreach (var slot in (await api.Send<TimetableSwapPlan>(HttpMethod.Post, "api/timetable/swap-plan", new TimetableSwapPlanRequest(Enumerable.Range(0, 5).Select(i => week.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray(), [], null))).Slots)
                        lessons[slot.Position] = slot.Lesson;
                bool Away(TimetableSuggestion idea) => idea.Exchanges.Any(e => !dates.Contains(e.First.Date) || !dates.Contains(e.Second.Date));
                string Lesson(TimetablePosition p, Func<string, string> name) => lessons.TryGetValue(p, out var l) ? $"{p.Period}교시 {l.Subject}({name(l.TeacherName)})" : p.Period + "교시";
                string Line(TimetableExchange e, Func<string, string> name) => $"{DayLabel(e.First.Date)} {(lessons.TryGetValue(e.First, out var l) ? ShortClass(l.ClassName) : "")} {Lesson(e.First, name)}  ↔  " +
                    (e.Second.Date == e.First.Date ? "" : DayLabel(e.Second.Date) + " ") + Lesson(e.Second, name);
                var order = Enumerable.Range(0, found.Length).ToArray(); string? said = null;
                if (wish.Text.Trim().Length > 0)
                {
                    if (!feature("ai")) note.Text = "학교에서 AI 사용을 꺼 두어 프로그램이 찾은 순서로 보여 줍니다.";
                    else
                    {
                        if (!Ai.Ready) new AiWindow(window, "요청을 반영해 교체안을 고르려면 AI를 연결하세요. 요청 글과 요일·교시·학급·과목만 보내고 선생님 이름은 기호로 바꿉니다.").ShowDialog();
                        if (!Ai.Ready) note.Text = "AI가 연결되지 않아 프로그램이 찾은 순서로 보여 줍니다.";
                        else
                        {
                            var names = new SwapAdvice.Names(api.Session?.User?.Name ?? "");
                            var prompt = SwapAdvice.Prompt(wish.Text, what, found.Select(f => (f.SameDay, f.Exchanges.Select(e => Line(e, names.Of)).ToArray())).ToArray());
                            find.Content = "AI가 고르는 중…";
                            try { (order, said) = SwapAdvice.Read(await Ai.Ask(SwapAdvice.Instructions, prompt, CancellationToken.None), found.Length); }
                            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or JsonException)
                            { note.Text = "AI 응답을 받지 못해 프로그램이 찾은 순서로 보여 줍니다. " + error.Message; }
                        }
                    }
                }
                if (said is { Length: > 0 })
                {
                    var advice = new StackPanel();
                    advice.Children.Add(AdminUi.Label(Ai.Name(Ai.InUse ?? Ai.Current.Use ?? Ai.ChatGpt) + "의 의견 · 교체안은 모두 프로그램이 규칙을 확인한 것입니다", "Faint", 11));
                    var text = AdminUi.Label(said, "Ink", 13); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 4, 0, 0); advice.Children.Add(text);
                    var box = AdminUi.Card(advice, 12); box.Margin = new Thickness(0, 0, 0, 8); results.Children.Add(box);
                }
                for (var rank = 0; rank < order.Length; rank++)
                {
                    var idea = found[order[rank]]; var lines = idea.Exchanges.Select(e => Line(e, n => n)).ToArray();
                    var card = new StackPanel();
                    var head = new DockPanel();
                    var use = new Button { Content = "이 안으로 바꾸기", Padding = new Thickness(12, 4, 12, 4) }; DockPanel.SetDock(use, Dock.Right);
                    use.SetResourceReference(StyleProperty, rank == 0 ? "PrimaryButton" : "SubtleButton");
                    AutomationProperties.SetName(use, $"교체안 {rank + 1} 적용");
                    head.Children.Add(use);
                    var title = AdminUi.Label($"교체안 {rank + 1} · {(idea.SameDay ? "같은 날" : Away(idea) ? "다른 주 포함" : "다른 날 포함")} · {idea.Exchanges.Length}쌍", "Ink", 13, true); title.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(title);
                    card.Children.Add(head);
                    foreach (var line in lines) { var t = AdminUi.Label(line, "Muted", 12.5); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 4, 0, 0); card.Children.Add(t); }
                    var frame = AdminUi.Card(card, 12); frame.Margin = new Thickness(0, 0, 0, 8); results.Children.Add(frame);
                    use.Click += async (_, _) => await Run(async () =>
                    {
                        if (mark.IsChecked == true)
                        {
                            var me = api.Session!.User!.Id;
                            var open = dates.Where(d => string.CompareOrdinal(d, today) >= 0).ToArray();
                            var times = (plan.Unavailable ?? []).Where(u => u.TeacherId == me && open.Contains(u.Date)).Select(u => new TimetableTime(u.Date, u.Period)).Concat(vacate).Distinct().ToArray();
                            await api.Send<TimetableUnavailable[]>(HttpMethod.Put, "api/timetable/unavailable", new TimetableUnavailableUpdate(open, times));
                        }
                        var replaced = boardMoves.Count;
                        boardWeek = monday; boardMoves.Clear(); boardAssume = false; boardRequestId = null;
                        boardMoves.Add(new(new TimetableSwapOption(idea.Exchanges[0].Second, idea.Exchanges), string.Join("\n", lines)));
                        if (boardReason.Length == 0) boardReason = what + " 비움 (교체 도우미)";
                        window.Close(); await Board();
                        feedback.Text = "교체안을 표에 반영했습니다. 바뀐 칸을 확인하고 ‘교체 신청 제출’을 누르세요." + (replaced > 0 ? " 표에서 바꾸던 내용은 지웠습니다." : "") + (Away(idea) ? " 다른 주 칸은 ‘›’로 넘겨 확인하세요." : "");
                    });
                }
                if (note.Text.Length == 0) note.Text = $"{what}을(를) 비우는 교체안 {found.Length}개를 찾았습니다." + (said is { Length: > 0 } ? " AI가 요청에 맞게 순서를 정했습니다." : " 같은 날 교체를 먼저 보여 줍니다.");
            }
            finally { find.IsEnabled = true; find.Content = "교체안 찾기"; }
        }
        find.Click += async (_, _) => { try { await Find(); } catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException) { note.Text = error.Message; } };
        return new(window, day, periods, wish, note, results, Find);
    }

    // The helper finds ways to empty a period of the teacher's, or says why there are none; nothing is applied.
    async Task VerifyHelper(TimetablePlanSlot pick, Action<Window, string> capture)
    {
        var parts = BuildHelper(); parts.Window.Show();
        parts.Day.SelectedDate = TimetableDate(pick.Position.Date);
        parts.Periods[Math.Clamp(pick.Position.Period, 1, parts.Periods.Length) - 1].IsChecked = true;
        await parts.Find(); parts.Window.UpdateLayout();
        if (parts.Note.Text.Length == 0) throw new InvalidOperationException("교체 도우미가 결과도 안내도 보여 주지 않았습니다.");
        capture(parts.Window, "timetable-helper");
        parts.Window.Close();
    }
}
