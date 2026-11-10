using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

// The whole-school board ("전체 시간표"): teachers down the side, the week's periods across. Picking a lesson lights every lesson it can
// trade places with; the server works those out, so the board never offers a trade the request would be refused for.
public sealed partial class TimetableWindow
{
    sealed record BoardMove(TimetableSwapOption Option, string Label);
    DateTime boardWeek;
    TimetableSwapPlan? boardPlan;
    readonly List<BoardMove> boardMoves = [];
    readonly Dictionary<TimetablePosition, Border> boardCells = [];
    Dictionary<TimetablePosition, TimetableSwapOption> boardOptions = [];
    TimetablePlanSlot? boardPick;
    Task boardPicking = Task.CompletedTask;
    bool boardAll, boardAssume;
    string[] boardDependencies = [];
    string? boardRequestId;
    string boardReason = "";
    double boardScrollX;
    Border? dragCell;
    Point dragFrom;

    string[] BoardDates => Enumerable.Range(0, 5).Select(i => boardWeek.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
    TimetableExchange[]? BoardExchanges => boardMoves.Count == 0 ? null : boardMoves.SelectMany(m => m.Option.Exchanges).ToArray();
    static string ShortClass(string name) { var m = Regex.Match(name, @"(\d+)\s*학년\s*(\d+)\s*반"); return m.Success ? m.Groups[1].Value + "-" + m.Groups[2].Value : name; }
    static string TeacherKey(TimetableSlot lesson) => lesson.TeacherId.Length > 0 ? lesson.TeacherId : "name:" + lesson.TeacherName;
    static string DayLabel(string date) => TimetableDate(date).ToString("M.d(ddd)", Korean);

    async Task SwapEditor()
    {
        boardWeek = selected.AddDays(-((int)selected.DayOfWeek + 6) % 7);
        boardMoves.Clear(); boardRequestId = null; boardReason = ""; boardAll = false; boardAssume = false; boardScrollX = 0;
        // The board is wide: a week of periods for every teacher.
        if (WindowState == WindowState.Normal && Width < 1240)
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(area.Width - 24, 1480); Left = area.Left + (area.Width - Width) / 2;
        }
        await Board();
    }

    async Task Board()
    {
        var requests = await api.Get<TimetableSwap[]>("api/timetable/swaps");
        var pending = requests.Where(s => s.Status == "pending").ToArray();
        boardDependencies = boardAssume ? pending.Select(s => s.Id).Order(StringComparer.Ordinal).ToArray() : [];
        boardPlan = await api.Send<TimetableSwapPlan>(HttpMethod.Post, "api/timetable/swap-plan",
            new TimetableSwapPlanRequest(BoardDates, boardDependencies, BoardExchanges));
        var offset = pageScroll.VerticalOffset;
        boardPick = null; boardOptions = []; boardCells.Clear();
        SchedulePage("수업 교체 신청");
        body.Children.Add(Text("옮길 수업을 누르면 바꿀 수 있는 수업이 초록색으로 표시됩니다. 초록 칸을 누르거나 그 칸으로 끌어다 놓으면 두 수업이 맞바뀝니다. 묶음수업은 함께 움직입니다."));

        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        Button Step(string label, int days, string name)
        {
            var button = new Button { Content = label, Width = 32, FontSize = 18, Padding = new Thickness(0), ToolTip = name };
            button.SetResourceReference(StyleProperty, "SubtleButton");
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += async (_, _) => await Run(async () => { boardWeek = boardWeek.AddDays(days); await Board(); });
            return button;
        }
        var week = AdminUi.Label(boardWeek.ToString("M.d") + " ~ " + boardWeek.AddDays(4).ToString("M.d"), "Ink", 13, true);
        week.VerticalAlignment = VerticalAlignment.Center; week.Margin = new Thickness(6, 0, 6, 0);
        tools.Children.Add(Step("‹", -7, "이전 주")); tools.Children.Add(week); tools.Children.Add(Step("›", 7, "다음 주"));
        CheckBox Toggle(string label, bool value, Action<bool> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            box.Click += async (_, _) => await Run(async () => { change(box.IsChecked == true); await Board(); });
            return box;
        }
        var mine = boardPlan.Slots.Where(s => s.Mine).ToArray();
        if (mine.Length > 0) tools.Children.Add(Toggle("모든 선생님 보기", boardAll, value => boardAll = value));
        if (pending.Length > 0 && boardMoves.Count == 0)
            tools.Children.Add(Toggle($"대기 중인 신청 {pending.Length}건이 승인된 것으로 보고 찾기", boardAssume, value => boardAssume = value));
        body.Children.Add(tools);

        var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Key(string label, string brush)
        {
            var chip = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.BackgroundProperty, brush); chip.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            var text = AdminUi.Label(label, "Muted", 12); text.Margin = new Thickness(5, 0, 14, 0);
            legend.Children.Add(chip); legend.Children.Add(text);
        }
        Key("내 수업", "Pale"); Key("바꿀 수 있는 수업", "SuccessTint"); Key("이번 신청으로 바뀌는 칸", "ChangeTint");
        legend.Children.Add(AdminUi.Label("◆ 묶음수업", "Muted", 12));
        body.Children.Add(legend);

        body.Children.Add(BoardGrid(pending));
        BoardRecord();
        _ = Dispatcher.BeginInvoke(new Action(() => pageScroll.ScrollToVerticalOffset(offset)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    FrameworkElement BoardGrid(TimetableSwap[] pending)
    {
        var plan = boardPlan!; var dates = BoardDates; var today = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (plan.Slots.Length == 0) return AdminUi.Empty("이 주에는 시간표가 없습니다", "다른 주로 이동하거나 기초시간표가 등록되었는지 확인하세요.");
        var periods = Math.Max(7, plan.Slots.Max(s => s.Position.Period));
        var mine = plan.Slots.Where(s => s.Mine).ToArray();
        var myRooms = mine.Select(s => s.Position.ClassId).ToHashSet();
        var changed = (BoardExchanges ?? []).SelectMany(e => new[] { e.First, e.Second }).ToHashSet();
        var waiting = pending.SelectMany(s => s.Changes.Select(c => c.Position)).ToHashSet();
        // A trade partner teaches one of my classes, so those teachers come first; the manager and "everyone" see the whole school.
        var rows = plan.Slots.Where(s => boardAll || mine.Length == 0 || s.Mine || myRooms.Contains(s.Position.ClassId))
            .GroupBy(s => TeacherKey(s.Lesson)).Select(g => (Key: g.Key, Name: g.First().Lesson.TeacherName, Mine: g.Any(s => s.Mine)))
            .OrderByDescending(t => t.Mine).ThenBy(t => t.Name, StringComparer.CurrentCulture).ToArray();
        var byTeacher = plan.Slots.ToLookup(s => (TeacherKey(s.Lesson), s.Position.Date, s.Position.Period));

        const double cellWidth = 46, cellHeight = 36, nameWidth = 92;
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nameWidth) });
        for (var c = 0; c < dates.Length * periods; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellWidth) });
        for (var r = 0; r < rows.Length + 2; r++) grid.RowDefinitions.Add(new RowDefinition { Height = r < 2 ? GridLength.Auto : new GridLength(cellHeight) });
        Border Place(Border cell, int row, int column, int span = 1)
        {
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column); if (span > 1) Grid.SetColumnSpan(cell, span);
            grid.Children.Add(cell); return cell;
        }
        Border Head(string text, bool daySeparator, bool strong = false, string brush = "Muted")
        {
            var label = AdminUi.Label(text, brush, 12, strong); label.HorizontalAlignment = HorizontalAlignment.Center;
            var cell = new Border { Child = label, Padding = new Thickness(0, 5, 0, 5), BorderThickness = new Thickness(daySeparator ? 2 : 0, 0, 0, 1) };
            cell.SetResourceReference(Border.BackgroundProperty, "Subtle"); cell.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            return cell;
        }
        Place(Head("교사", false), 0, 0); Place(Head("", false), 1, 0);
        for (var d = 0; d < dates.Length; d++)
        {
            var isToday = dates[d] == today;
            Place(Head(DayLabel(dates[d]) + (isToday ? " · 오늘" : ""), true, true, isToday ? "Blue" : "Ink"), 0, 1 + d * periods, periods);
            for (var p = 1; p <= periods; p++) Place(Head(p.ToString(), p == 1), 1, d * periods + p);
        }
        for (var r = 0; r < rows.Length; r++)
        {
            var teacher = rows[r];
            var name = AdminUi.Label(teacher.Name, teacher.Mine ? "Blue" : "Ink", 12, teacher.Mine);
            name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(8, 0, 4, 0); name.TextTrimming = TextTrimming.CharacterEllipsis;
            var side = new Border { Child = name, BorderThickness = new Thickness(0, 0, 0, 1), ToolTip = teacher.Name };
            side.SetResourceReference(Border.BackgroundProperty, teacher.Mine ? "Pale" : "Subtle"); side.SetResourceReference(Border.BorderBrushProperty, "Line");
            Place(side, r + 2, 0);
            for (var d = 0; d < dates.Length; d++)
                for (var p = 1; p <= periods; p++)
                {
                    var lessons = byTeacher[(teacher.Key, dates[d], p)].ToArray();
                    var cell = new Border { Tag = "empty", AllowDrop = true, Background = System.Windows.Media.Brushes.Transparent };
                    if (lessons.Length > 0)
                    {
                        var first = lessons[0];
                        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        var room = AdminUi.Label(ShortClass(first.Lesson.ClassName) + (lessons.Length > 1 ? "+" + (lessons.Length - 1) : "") + (first.BundleName is null ? "" : "◆"),
                            first.Mine ? "Blue" : "Ink", 11.5, true);
                        var subject = AdminUi.Label(first.Lesson.Subject, "Muted", 10.5);
                        foreach (var line in new[] { room, subject }) { line.HorizontalAlignment = HorizontalAlignment.Center; line.TextTrimming = TextTrimming.CharacterEllipsis; line.TextWrapping = TextWrapping.NoWrap; content.Children.Add(line); }
                        cell.Child = content;
                        cell.Tag = lessons.Any(l => changed.Contains(l.Position)) ? "changed" : first.Mine ? "mine" : "lesson";
                        cell.ToolTip = string.Join("\n", lessons.Select(l => $"{l.Lesson.ClassName} {l.Position.Period}교시 · {l.Lesson.Subject} ({l.Lesson.TeacherName})" +
                            (l.BundleName is null ? "" : " · 묶음 " + l.BundleName) + (changed.Contains(l.Position) ? " · 이번 신청으로 바뀜" : "") + (waiting.Contains(l.Position) ? " · 대기 중인 신청 있음" : "")));
                        cell.Cursor = Cursors.Hand;
                        if (string.CompareOrdinal(dates[d], today) < 0) cell.Opacity = 0.45;
                        foreach (var lesson in lessons) boardCells[lesson.Position] = cell;
                        System.Windows.Automation.AutomationProperties.SetName(cell, $"{teacher.Name} {DayLabel(dates[d])} {p}교시 {first.Lesson.ClassName} {first.Lesson.Subject}");
                    }
                    BoardPaint(cell, p == 1);
                    cell.MouseLeftButtonDown += (_, e) => { dragCell = cell; dragFrom = e.GetPosition(this); BoardPressed(lessons); };
                    cell.MouseMove += (_, e) =>
                    {
                        if (e.LeftButton != MouseButtonState.Pressed || dragCell != cell || lessons.Length == 0 || (e.GetPosition(this) - dragFrom).Length < 6) return;
                        dragCell = null;
                        try { DragDrop.DoDragDrop(cell, new DataObject("GyomutalkLesson", "lesson"), DragDropEffects.Move); }
                        catch (System.Runtime.InteropServices.COMException) { /* A drag the system refused: the pick stays, so a click still works. */ }
                    };
                    cell.DragOver += (_, e) => { e.Effects = lessons.Any(l => boardOptions.ContainsKey(l.Position)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
                    cell.Drop += async (_, e) =>
                    {
                        e.Handled = true; await boardPicking;
                        if (lessons.Select(l => l.Position).FirstOrDefault(boardOptions.ContainsKey) is { } target) await Run(() => BoardApply(boardOptions[target]));
                    };
                    Place(cell, r + 2, d * periods + p);
                }
        }
        var frame = new Border { Child = grid, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        var scroll = new ScrollViewer { Content = frame, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 0, 6) };
        scroll.ScrollChanged += (_, e) => { if (e.HorizontalChange != 0) boardScrollX = e.HorizontalOffset; };
        scroll.Loaded += (_, _) => scroll.ScrollToHorizontalOffset(boardScrollX);
        // The page scrolls up and down; the wheel must not stop at the board.
        scroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; pageScroll.ScrollToVerticalOffset(pageScroll.VerticalOffset - e.Delta / 3.0); } };
        return scroll;
    }

    // A cell looks like what it holds unless it is picked or offered as a trade.
    static void BoardPaint(Border cell, bool dayStart, string? overlay = null)
    {
        var kind = overlay ?? (string)cell.Tag;
        if (kind == "empty") cell.Background = System.Windows.Media.Brushes.Transparent;
        else cell.SetResourceReference(Border.BackgroundProperty, kind switch { "option" => "SuccessTint", "changed" => "ChangeTint", "mine" or "pick" => "Pale", _ => "Surface" });
        cell.SetResourceReference(Border.BorderBrushProperty, kind switch { "option" => "Success", "pick" => "Blue", _ => dayStart ? "LineStrong" : "Line" });
        cell.BorderThickness = kind is "option" or "pick" ? new Thickness(2) : new Thickness(dayStart ? 2 : 1, 0, 0, 1);
    }
    bool IsDayStart(Border cell)
    {
        var periods = (((Grid)cell.Parent).ColumnDefinitions.Count - 1) / BoardDates.Length;
        return (Grid.GetColumn(cell) - 1) % periods == 0;
    }

    void BoardPressed(TimetablePlanSlot[] lessons)
    {
        if (busy) return;
        if (boardPick is not null && lessons.Select(l => l.Position).FirstOrDefault(boardOptions.ContainsKey) is { } target)
        {
            dragCell = null; var option = boardOptions[target];
            _ = Run(() => BoardApply(option));
            return;
        }
        if (lessons.Length == 0) { BoardClearPick(); feedback.Text = ""; return; }
        var slot = lessons.FirstOrDefault(l => l.Mine) ?? lessons[0];
        if (boardPick?.Position == slot.Position) return;
        boardPicking = BoardPickLesson(slot);
    }

    void BoardClearPick()
    {
        boardPick = null; boardOptions = [];
        foreach (var cell in boardCells.Values.Distinct()) BoardPaint(cell, IsDayStart(cell));
    }

    async Task BoardPickLesson(TimetablePlanSlot slot)
    {
        BoardClearPick();
        if (!slot.Mine && !CanManageSchedule && slot.BundleName is null)
        { feedback.Text = "내 수업만 옮길 수 있습니다. 내 수업은 파란색으로 표시됩니다."; return; }
        try
        {
            var options = await api.Send<TimetableSwapOption[]>(HttpMethod.Post, "api/timetable/swap-options",
                new TimetableSwapOptionsRequest(BoardDates, boardDependencies, BoardExchanges, slot.Position));
            boardPick = slot; boardOptions = options.ToDictionary(o => o.Target);
            if (boardCells.TryGetValue(slot.Position, out var picked)) BoardPaint(picked, IsDayStart(picked), "pick");
            foreach (var option in options) if (boardCells.TryGetValue(option.Target, out var cell)) BoardPaint(cell, IsDayStart(cell), "option");
            var hidden = options.Count(o => !boardCells.ContainsKey(o.Target));
            var what = $"{slot.Lesson.ClassName} {DayLabel(slot.Position.Date)} {slot.Position.Period}교시 {slot.Lesson.Subject}";
            feedback.Text = options.Length == 0
                ? what + ": 이번 주에는 바꿀 수 있는 수업이 없습니다. 상대 선생님의 다른 수업과 겹치거나 지난 수업입니다. 다른 주를 확인해 보세요."
                : what + $": 초록색 {options.Length - hidden}칸과 바꿀 수 있습니다." + (hidden > 0 ? $" ‘모든 선생님 보기’를 켜면 {hidden}칸이 더 보입니다." : "");
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        { feedback.Text = error.Message; }
    }

    async Task BoardApply(TimetableSwapOption option)
    {
        if ((BoardExchanges?.Length ?? 0) + option.Exchanges.Length > 12)
            throw new InvalidOperationException("한 번에 신청할 수 있는 교체는 12쌍까지입니다. 지금까지 바꾼 내용을 먼저 신청하세요.");
        var slots = boardPlan!.Slots.ToDictionary(s => s.Position, s => s.Lesson);
        string Lesson(TimetablePosition p) => slots.TryGetValue(p, out var l) ? $"{p.Period}교시 {l.Subject}({l.TeacherName})" : p.Period + "교시";
        string Line(TimetableExchange e) => $"{DayLabel(e.First.Date)} {(slots.TryGetValue(e.First, out var l) ? l.ClassName : "")} {Lesson(e.First)}  ↔  " +
            (e.Second.Date == e.First.Date ? "" : DayLabel(e.Second.Date) + " ") + Lesson(e.Second);
        boardMoves.Add(new(option, string.Join("\n", option.Exchanges.Select((e, i) => (i == 0 ? "" : "함께 이동(묶음) · ") + Line(e)))));
        boardRequestId = null;
        await Board();
        feedback.Text = option.Exchanges.Length > 1 ? $"수업을 맞바꿨습니다. 묶음수업 {option.Exchanges.Length}개 학급이 함께 움직였습니다." : "수업을 맞바꿨습니다. 아래 ‘변경 기록’을 확인한 뒤 신청하세요.";
    }

    // Everything this request changes, in the order it was done.
    void BoardRecord()
    {
        var list = new StackPanel();
        list.Children.Add(AdminUi.Heading($"변경 기록 · {boardMoves.Count}건", 14));
        if (boardMoves.Count == 0) { var none = AdminUi.Label("아직 바꾼 수업이 없습니다. 위 표에서 옮길 수업을 누르세요.", "Muted", 12); none.Margin = new Thickness(0, 6, 0, 0); list.Children.Add(none); }
        for (var i = 0; i < boardMoves.Count; i++)
        {
            var line = AdminUi.Label($"{i + 1}. {boardMoves[i].Label}", "Ink", 12.5); line.Margin = new Thickness(0, 8, 0, 0); line.TextWrapping = TextWrapping.Wrap;
            list.Children.Add(line);
        }
        if (boardMoves.Count > 0)
        {
            var edits = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            Button Edit(string label, Action change)
            {
                var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) }; button.SetResourceReference(StyleProperty, "SubtleButton");
                button.Click += async (_, _) => await Run(async () => { change(); boardRequestId = null; await Board(); });
                return button;
            }
            edits.Children.Add(Edit("마지막 변경 되돌리기", () => boardMoves.RemoveAt(boardMoves.Count - 1)));
            if (boardMoves.Count > 1) edits.Children.Add(Edit("모두 되돌리기", boardMoves.Clear));
            list.Children.Add(edits);
        }
        var card = AdminUi.Card(list, 16); card.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(card);
        body.Children.Add(AdminUi.Label("신청 사유 (시간표 담당자와 본인에게만 보입니다)", "Muted", 12));
        var reason = new TextBox { MaxLength = 500, Text = boardReason, Margin = new Thickness(0, 6, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(reason, "신청 사유");
        reason.TextChanged += (_, _) => { boardReason = reason.Text; boardRequestId = null; };
        body.Children.Add(reason);
        var submit = new Button { Content = "교체 신청 제출", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = boardMoves.Count > 0 };
        submit.SetResourceReference(StyleProperty, "PrimaryButton");
        submit.Click += async (_, _) => await Run(async () =>
        {
            boardRequestId ??= NewScheduleId();
            await api.Send<TimetableSwap>(HttpMethod.Post, "api/timetable/swaps",
                new TimetableSwapCreate(boardRequestId, boardPlan!.Revision, BoardExchanges!, boardDependencies, boardReason));
            boardMoves.Clear();
            await SwapQueue();
            feedback.Text = "교체 신청을 접수했습니다. 시간표 담당자가 승인하면 시간표에 반영되고 관련 선생님께 알림이 갑니다.";
        });
        body.Children.Add(submit);
    }

    // The base timetable and the lessons that move together.
    async Task BaseEditor()
    {
        var rules = await api.Get<TimetableBundleRule[]>("api/timetable/bundle-rules");
        var lessons = await api.Get<TimetableBaseLesson[]>("api/timetable/base-lessons");
        var history = await api.Get<JsonElement[]>("api/timetable/history");
        SchedulePage("기초시간표 · 묶음수업");

        var basePanel = new StackPanel();
        basePanel.Children.Add(AdminUi.Heading("기초시간표", 15));
        var latest = history.FirstOrDefault(r => r.GetProperty("kind").GetString() == "base" && !r.GetProperty("withdrawn").GetBoolean());
        var status = lessons.Length == 0 ? "아직 등록된 기초시간표가 없습니다."
            : $"{lessons.Select(l => l.ClassId).Distinct().Count()}개 학급의 기초시간표가 등록되어 있습니다." +
              (latest.ValueKind == JsonValueKind.Object ? $"\n최근 등록 · {latest.GetProperty("fileName").GetString()} · 적용 {latest.GetProperty("start").GetString()} ~ {latest.GetProperty("end").GetString()}" : "");
        var statusLabel = AdminUi.Label(status, "Ink", 13); statusLabel.Margin = new Thickness(0, 8, 0, 4); basePanel.Children.Add(statusLabel);
        var how = AdminUi.Label("나이스에서 내려받은 학급별 기초시간표 엑셀(.xlsx)을 올립니다. 학기 중에 시간표가 바뀌면 새 파일을 다시 올리면 됩니다.", "Muted", 12);
        how.Margin = new Thickness(0, 0, 0, 12); basePanel.Children.Add(how);
        var upload = new Button { Content = lessons.Length == 0 ? "기초시간표 등록" : "기초시간표 다시 등록", HorizontalAlignment = HorizontalAlignment.Left };
        upload.SetResourceReference(StyleProperty, "PrimaryButton"); upload.Click += async (_, _) => await Run(Choose);
        basePanel.Children.Add(upload);
        var baseCard = AdminUi.Card(basePanel, 18); baseCard.Margin = new Thickness(0, 4, 0, 12); body.Children.Add(baseCard);

        var bundlePanel = new StackPanel();
        bundlePanel.Children.Add(AdminUi.Heading("묶음수업", 15));
        var about = AdminUi.Label("여러 반이 같은 시간에 나뉘어 듣는 수업을 묶어 두면, 수업을 교체할 때 그 시간의 수업이 모두 함께 움직입니다. 같은 과목이라도 반마다 따로 고를 수 있습니다.", "Muted", 12);
        about.Margin = new Thickness(0, 8, 0, 10); bundlePanel.Children.Add(about);
        var names = lessons.ToDictionary(l => new TimetableBundleMember(l.ClassId, l.Subject), l => l.ClassName + " " + l.Subject);
        foreach (var rule in rules.OrderBy(r => r.Name, StringComparer.CurrentCulture))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var remove = new Button { Content = "삭제", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top }; remove.SetResourceReference(StyleProperty, "DangerButton");
            System.Windows.Automation.AutomationProperties.SetName(remove, rule.Name + " 묶음 삭제");
            remove.Click += async (_, _) => await Run(async () =>
            {
                if (Notice.Show(this, $"‘{rule.Name}’ 묶음을 삭제할까요?", "묶음수업", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await api.Send<object>(HttpMethod.Delete, "api/timetable/bundle-rules/" + rule.Id);
                await BaseEditor();
            });
            DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var text = new StackPanel(); text.Children.Add(AdminUi.Label("◆ " + rule.Name, "Ink", 13, true));
            var members = AdminUi.Label(string.Join(", ", rule.Members.Select(m => names.GetValueOrDefault(m, m.Subject + " (지금 시간표에 없음)"))), "Muted", 12); members.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(members); row.Children.Add(text); bundlePanel.Children.Add(row);
        }
        if (rules.Length == 0) bundlePanel.Children.Add(AdminUi.Label("아직 지정한 묶음수업이 없습니다.", "Faint", 12));

        if (lessons.Length > 0)
        {
            var taken = rules.SelectMany(r => r.Members).ToHashSet();
            var chosen = new HashSet<TimetableBundleMember>();
            var add = AdminUi.Group("새 묶음 만들기"); add.Margin = new Thickness(0, 14, 0, 8); bundlePanel.Children.Add(add);
            var filters = new WrapPanel();
            var grade = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 8), ItemsSource = new[] { new Choice("", "전체 학년") }.Concat(lessons.Select(l => l.Grade).Distinct().Order().Select(g => new Choice(g.ToString(), g + "학년"))).ToArray(), SelectedIndex = 0 };
            System.Windows.Automation.AutomationProperties.SetName(grade, "학년");
            var search = new TextBox { Width = 200, Margin = new Thickness(0, 0, 8, 8), ToolTip = "과목이나 교사 이름으로 찾기" };
            System.Windows.Automation.AutomationProperties.SetName(search, "과목·교사 찾기");
            filters.Children.Add(grade); filters.Children.Add(search); filters.Children.Add(AdminUi.Label("과목이나 교사 이름으로 찾아 함께 움직일 수업을 모두 체크하세요.", "Muted", 12));
            bundlePanel.Children.Add(filters);
            var picks = new StackPanel();
            var picked = AdminUi.Label("", "Blue", 12); picked.TextWrapping = TextWrapping.Wrap; picked.Margin = new Thickness(0, 8, 0, 8);
            void Summary() => picked.Text = chosen.Count == 0 ? "선택한 수업이 없습니다." : $"선택 {chosen.Count}개 · " + string.Join(", ", chosen.Select(m => names[m]));
            void Fill()
            {
                picks.Children.Clear();
                var level = (grade.SelectedItem as Choice)?.Id ?? ""; var query = search.Text.Trim();
                foreach (var lesson in lessons.Where(l => (level.Length == 0 || l.Grade.ToString() == level) &&
                    (query.Length == 0 || l.Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase) || l.Teachers.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                    .OrderBy(l => l.Subject, StringComparer.CurrentCulture).ThenBy(l => l.Grade).ThenBy(l => l.ClassName, StringComparer.CurrentCulture))
                {
                    var member = new TimetableBundleMember(lesson.ClassId, lesson.Subject); var used = taken.Contains(member);
                    var box = new CheckBox { Content = $"{lesson.ClassName} · {lesson.Subject} ({lesson.Teachers}) · 주 {lesson.Hours}시간" + (used ? " · 이미 묶음에 있음" : ""), IsChecked = chosen.Contains(member), IsEnabled = !used, Margin = new Thickness(0, 3, 0, 3) };
                    box.Click += (_, _) => { if (box.IsChecked == true) chosen.Add(member); else chosen.Remove(member); Summary(); };
                    picks.Children.Add(box);
                }
                if (picks.Children.Count == 0) picks.Children.Add(AdminUi.Label("조건에 맞는 수업이 없습니다.", "Faint", 12));
            }
            grade.SelectionChanged += (_, _) => Fill(); search.TextChanged += (_, _) => Fill(); Fill(); Summary();
            var listFrame = new Border { Child = new ScrollViewer { Content = picks, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 6, 10, 6) }, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
            listFrame.SetResourceReference(Border.BorderBrushProperty, "Line"); bundlePanel.Children.Add(listFrame); bundlePanel.Children.Add(picked);
            var name = new TextBox { MaxLength = 60, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
            System.Windows.Automation.AutomationProperties.SetName(name, "묶음 이름");
            bundlePanel.Children.Add(AdminUi.Label("묶음 이름 (예: 3학년 선택 A)", "Muted", 12)); bundlePanel.Children.Add(name);
            var save = new Button { Content = "묶음으로 저장", HorizontalAlignment = HorizontalAlignment.Left }; save.SetResourceReference(StyleProperty, "PrimaryButton");
            save.Click += async (_, _) => await Run(async () =>
            {
                if (chosen.Count < 2) throw new InvalidOperationException("함께 움직일 수업을 둘 이상 체크하세요.");
                var title = name.Text.Trim().Length > 0 ? name.Text.Trim() : string.Join("·", chosen.Select(m => m.Subject).Distinct().Take(3)) + " 묶음";
                await api.Send<TimetableBundleRule>(HttpMethod.Post, "api/timetable/bundle-rules", new TimetableBundleRule(NewScheduleId(), title, chosen.ToArray()));
                await BaseEditor();
                feedback.Text = $"‘{title}’ 묶음을 저장했습니다.";
            });
            bundlePanel.Children.Add(save);
        }
        else bundlePanel.Children.Add(AdminUi.Label("기초시간표를 먼저 등록하면 묶을 수업을 고를 수 있습니다.", "Muted", 12));
        body.Children.Add(AdminUi.Card(bundlePanel, 18));
    }

    // Walks the trade the way a teacher does: open the board, pick a lesson, take an offered trade, send the request.
    internal async Task VerifyBoard(Action<string> capture)
    {
        await SwapEditor(); UpdateLayout();
        if (boardPlan is null || boardCells.Count == 0) throw new InvalidOperationException("수업 교체 표가 비어 있습니다.");
        var today = DateTime.UtcNow.AddHours(9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var pick = boardPlan.Slots.Where(s => s.Mine && string.CompareOrdinal(s.Position.Date, today) >= 0).OrderBy(s => s.Position.Date).ThenBy(s => s.Position.Period).FirstOrDefault();
        if (pick is null) { capture("timetable-board"); await Load(); return; }
        await BoardPickLesson(pick);
        if (boardOptions.Count == 0) throw new InvalidOperationException("바꿀 수 있는 칸이 표시되지 않았습니다: " + feedback.Text);
        if (boardOptions.Keys.Any(p => string.CompareOrdinal(p.Date, today) < 0 || p.ClassId != pick.Position.ClassId)) throw new InvalidOperationException("지난 수업이나 다른 학급이 교체 대상으로 표시되었습니다.");
        UpdateLayout(); capture("timetable-board");
        var option = boardOptions.Values.OrderBy(o => o.Target.Date).ThenBy(o => o.Target.Period).First();
        await BoardApply(option); UpdateLayout();
        var moved = boardPlan!.Slots.Single(s => s.Position == option.Target).Lesson;
        if (boardMoves.Count != 1 || moved.Subject != pick.Lesson.Subject || (string)boardCells[option.Target].Tag != "changed") throw new InvalidOperationException("맞바꾼 수업이 표와 변경 기록에 반영되지 않았습니다.");
        capture("timetable-board-changed");
        boardReason = "검증용 교체";
        boardRequestId = NewScheduleId();
        await api.Send<TimetableSwap>(HttpMethod.Post, "api/timetable/swaps", new TimetableSwapCreate(boardRequestId, boardPlan.Revision, BoardExchanges!, boardDependencies, boardReason));
        boardMoves.Clear();
        await SwapQueue(); UpdateLayout(); capture("timetable-swap-queue");
        await BaseEditor(); UpdateLayout(); capture("timetable-base");
        await Load();
    }

    // The generator solves the timetable in the browser, so it opens in its own window beside the messenger.
    Task OpenGenerator()
    {
        const string site = "https://timetablecreator.vercel.app/";
        var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
        try
        {
            if (edge is null) Process.Start(new ProcessStartInfo(site) { UseShellExecute = true })?.Dispose();
            else { var start = new ProcessStartInfo(edge) { UseShellExecute = false }; start.ArgumentList.Add("--app=" + site); Process.Start(start)?.Dispose(); }
            feedback.Text = "시간표 생성기를 새 창으로 열었습니다.";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { feedback.Text = "시간표 생성기를 열지 못했습니다. 브라우저에서 " + site + " 를 직접 열어 주세요."; }
        return Task.CompletedTask;
    }
}
