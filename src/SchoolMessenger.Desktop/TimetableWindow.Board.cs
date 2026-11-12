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
    double boardScrollX, boardScrollY;
    TextBlock? boardHint;
    ScrollViewer? boardRows;
    Button? boardSubmit;
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
        boardMoves.Clear(); boardRequestId = null; boardReason = ""; boardAll = false; boardAssume = false; boardScrollX = boardScrollY = 0;
        // The board is wide and tall: a week of periods for every teacher. It takes the room the screen has.
        if (WindowState == WindowState.Normal)
        {
            var area = SystemParameters.WorkArea;
            if (Width < 1240) { Width = Math.Min(area.Width - 24, 1480); Left = area.Left + (area.Width - Width) / 2; }
            if (Height < area.Height - 24) { Height = Math.Min(area.Height - 24, 1100); Top = area.Top + (area.Height - Height) / 2; }
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
        boardPick = null; boardOptions = []; boardCells.Clear();
        ResetBody();
        // The board fills the window: the title and days stay on top, the request stays at the bottom, only the teachers scroll.
        var page = new DockPanel { Margin = new Thickness(16, 10, 16, 12) };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var back = new Button { Content = "‹  시간표로", Margin = new Thickness(-8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        back.SetResourceReference(StyleProperty, "SubtleButton"); back.Click += async (_, _) => await Run(Load);
        head.Children.Add(back);
        var title = AdminUi.Heading("수업 교체 신청", 17); title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(0, 0, 14, 0);
        head.Children.Add(title);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void Key(string label, string brush)
        {
            var chip = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.BackgroundProperty, brush); chip.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            var text = AdminUi.Label(label, "Muted", 12); text.Margin = new Thickness(5, 0, 12, 0); text.TextWrapping = TextWrapping.NoWrap;
            legend.Children.Add(chip); legend.Children.Add(text);
        }
        Key("내 수업", "Pale"); Key("바꿀 수 있는 수업", "SuccessTint"); Key("이번 신청으로 바뀐 칸", "ChangeTint");
        var bundleKey = AdminUi.Label("◆ 묶음수업", "Muted", 12); bundleKey.TextWrapping = TextWrapping.NoWrap; legend.Children.Add(bundleKey);

        var tools = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        Button Step(string label, int days, string name)
        {
            var button = new Button { Content = label, Width = 30, FontSize = 18, Padding = new Thickness(0), ToolTip = name };
            button.SetResourceReference(StyleProperty, "SubtleButton");
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += async (_, _) => await Run(async () => { boardWeek = boardWeek.AddDays(days); await Board(); });
            return button;
        }
        var week = AdminUi.Label(boardWeek.ToString("M.d") + " ~ " + boardWeek.AddDays(4).ToString("M.d"), "Ink", 13, true);
        week.VerticalAlignment = VerticalAlignment.Center; week.Margin = new Thickness(4, 0, 4, 0); week.TextWrapping = TextWrapping.NoWrap;
        tools.Children.Add(Step("‹", -7, "이전 주")); tools.Children.Add(week); tools.Children.Add(Step("›", 7, "다음 주"));
        CheckBox Toggle(string label, bool value, Action<bool> change)
        {
            var box = new CheckBox { Content = label, IsChecked = value, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            box.Click += async (_, _) => await Run(async () => { change(box.IsChecked == true); await Board(); });
            return box;
        }
        var mine = boardPlan.Slots.Where(s => s.Mine).ToArray();
        if (mine.Length > 0) tools.Children.Add(Toggle("모든 선생님 보기", boardAll, value => boardAll = value));
        if (pending.Length > 0 && boardMoves.Count == 0)
            tools.Children.Add(Toggle($"대기 중인 신청 {pending.Length}건을 승인된 것으로 보기", boardAssume, value => boardAssume = value));
        head.Children.Add(tools);
        DockPanel.SetDock(head, Dock.Top); page.Children.Add(head);

        // One line under the title: how to use the board, or what just happened.
        var status = new Grid { MinHeight = 18 };
        boardHint = AdminUi.Label("옮길 수업을 누르면 바꿀 수 있는 수업이 초록색으로 표시됩니다. 초록 칸을 누르거나 그 칸으로 끌어다 놓으면 두 수업이 맞바뀝니다.", "Muted", 12);
        boardHint.TextTrimming = TextTrimming.CharacterEllipsis; boardHint.TextWrapping = TextWrapping.NoWrap;
        feedback.Margin = new Thickness(0); status.Children.Add(boardHint); status.Children.Add(feedback);
        boardHint.Visibility = feedback.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        var under = new DockPanel { Margin = new Thickness(0, 2, 0, 6) }; DockPanel.SetDock(legend, Dock.Right); legend.Margin = new Thickness(12, 0, 0, 0); legend.VerticalAlignment = VerticalAlignment.Top;
        under.Children.Add(legend); under.Children.Add(status);
        DockPanel.SetDock(under, Dock.Top); page.Children.Add(under);

        var record = BoardRecord(); DockPanel.SetDock(record, Dock.Bottom); page.Children.Add(record);
        page.Children.Add(BoardGrid(pending));
        fixedPage.Child = page; fixedPage.Visibility = Visibility.Visible; pageScroll.Visibility = Visibility.Collapsed;
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

        // Every lesson column shares the width left after the names, so a week always fits the window; below minCell the board scrolls sideways.
        const double nameWidth = 78, minCell = 30, minRow = 13, maxRow = 38, scrollRow = 32;
        var columns = dates.Length * periods;
        Grid Table()
        {
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(nameWidth) });
            for (var c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition());
            return g;
        }
        var header = Table(); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var grid = Table(); for (var r = 0; r < rows.Length; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(maxRow) });
        static Border Place(Grid table, Border cell, int row, int column, int span = 1)
        {
            Grid.SetRow(cell, row); Grid.SetColumn(cell, column); if (span > 1) Grid.SetColumnSpan(cell, span);
            table.Children.Add(cell); return cell;
        }
        Border Head(string text, bool daySeparator, bool strong = false, string brush = "Muted")
        {
            var label = AdminUi.Label(text, brush, 12, strong); label.HorizontalAlignment = HorizontalAlignment.Center; label.TextWrapping = TextWrapping.NoWrap;
            var cell = new Border { Child = label, Padding = new Thickness(0, 3, 0, 3), BorderThickness = new Thickness(daySeparator ? 2 : 0, 0, 0, 1) };
            cell.SetResourceReference(Border.BackgroundProperty, "Subtle"); cell.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            return cell;
        }
        Place(header, Head("교사", false), 0, 0); Place(header, Head("", false), 1, 0);
        for (var d = 0; d < dates.Length; d++)
        {
            var isToday = dates[d] == today;
            Place(header, Head(DayLabel(dates[d]) + (isToday ? " · 오늘" : ""), true, true, isToday ? "Blue" : "Ink"), 0, 1 + d * periods, periods);
            for (var p = 1; p <= periods; p++) Place(header, Head(p.ToString(), p == 1), 1, d * periods + p);
        }
        var names = new List<TextBlock>(); var texts = new List<(TextBlock Text, System.Windows.Documents.Run Gap, System.Windows.Documents.Run Subject, string Name)>();
        for (var r = 0; r < rows.Length; r++)
        {
            var teacher = rows[r];
            var name = AdminUi.Label(teacher.Name, teacher.Mine ? "Blue" : "Ink", 12, teacher.Mine);
            name.VerticalAlignment = VerticalAlignment.Center; name.Margin = new Thickness(8, 0, 4, 0); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
            names.Add(name);
            var side = new Border { Child = name, BorderThickness = new Thickness(0, 0, 0, 1), ToolTip = teacher.Name };
            side.SetResourceReference(Border.BackgroundProperty, teacher.Mine ? "Pale" : "Subtle"); side.SetResourceReference(Border.BorderBrushProperty, "Line");
            Place(grid, side, r, 0);
            for (var d = 0; d < dates.Length; d++)
                for (var p = 1; p <= periods; p++)
                {
                    var lessons = byTeacher[(teacher.Key, dates[d], p)].ToArray();
                    var cell = new Border { Tag = "empty", AllowDrop = true, Background = System.Windows.Media.Brushes.Transparent };
                    if (lessons.Length > 0)
                    {
                        var first = lessons[0];
                        // Class over subject on tall rows; on short rows the same two pieces sit on one line.
                        var room = new System.Windows.Documents.Run(ShortClass(first.Lesson.ClassName) + (lessons.Length > 1 ? "+" + (lessons.Length - 1) : "") + (first.BundleName is null ? "" : "◆")) { FontWeight = FontWeights.SemiBold };
                        room.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, first.Mine ? "Blue" : "Ink");
                        var gap = new System.Windows.Documents.Run("\n");
                        var subject = new System.Windows.Documents.Run(first.Lesson.Subject);
                        subject.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Muted");
                        var text = new TextBlock { TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(1, 0, 1, 0), FontSize = 11 };
                        text.Inlines.Add(room); text.Inlines.Add(gap); text.Inlines.Add(subject); texts.Add((text, gap, subject, first.Lesson.Subject));
                        cell.Child = text;
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
                    Place(grid, cell, r, d * periods + p);
                }
        }
        var headScroll = new ScrollViewer { Content = header, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        headScroll.SetResourceReference(Control.BackgroundProperty, "Subtle");
        var rowScroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        boardRows = rowScroll;
        rowScroll.ScrollChanged += (_, e) =>
        {
            headScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
            if (e.HorizontalChange != 0) boardScrollX = e.HorizontalOffset;
            if (e.VerticalChange != 0) boardScrollY = e.VerticalOffset;
        };
        rowScroll.Loaded += (_, _) => { rowScroll.ScrollToHorizontalOffset(boardScrollX); rowScroll.ScrollToVerticalOffset(boardScrollY); };
        // Rows shrink until every teacher fits the window. When even the smallest readable row would not fit, the list scrolls
        // instead, and then the rows are tall enough for class and subject on two lines.
        void Fit()
        {
            if (rowScroll.ActualWidth <= 0 || rowScroll.ActualHeight <= 0) return;
            var bar = SystemParameters.VerticalScrollBarWidth; var minWidth = nameWidth + columns * minCell;
            var height = rowScroll.ActualHeight - (minWidth > rowScroll.ActualWidth ? SystemParameters.HorizontalScrollBarHeight : 0);
            var fits = rows.Length * minRow <= height;
            var row = fits ? Math.Min(Math.Floor(height / rows.Length), maxRow) : scrollRow;
            var width = Math.Max(minWidth, Math.Floor(rowScroll.ActualWidth - (fits ? 0 : bar)));
            grid.Width = header.Width = width;
            foreach (var definition in grid.RowDefinitions) definition.Height = new GridLength(row);
            // One line holds the class and the subject only in a wide cell; a narrow one keeps the class, the subject stays in the tooltip.
            var twoLines = row >= 30; var subjects = twoLines || (width - nameWidth) / columns >= 56; var size = row >= 30 ? 11 : row >= 20 ? 10.5 : row >= 15 ? 9.5 : 9;
            foreach (var (text, gap, subject, lesson) in texts) { text.FontSize = size; gap.Text = !subjects ? "" : twoLines ? "\n" : " "; subject.Text = subjects ? lesson : ""; }
            foreach (var name in names) name.FontSize = row >= 20 ? 12 : row >= 15 ? 10.5 : 9.5;
        }
        rowScroll.SizeChanged += (_, _) => Fit();
        var layout = new DockPanel(); DockPanel.SetDock(headScroll, Dock.Top); layout.Children.Add(headScroll); layout.Children.Add(rowScroll);
        var frame = new Border { Child = layout, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        return frame;
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
            // When the teachers scroll, an offered trade may sit below the fold: bring the nearest one into view.
            if (boardRows is { } list)
            {
                bool Seen(Border cell) { var top = cell.TransformToAncestor(list).Transform(new Point()).Y; return top >= 0 && top + cell.ActualHeight <= list.ViewportHeight; }
                var offered = options.Select(o => boardCells.GetValueOrDefault(o.Target)).OfType<Border>().ToArray();
                if (offered.Length > 0 && !offered.Any(Seen)) offered[0].BringIntoView();
            }
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

    // Sending the request sits right under the board; below it, everything this request changes in the order it was done.
    FrameworkElement BoardRecord()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var send = new DockPanel();
        var submit = new Button { Content = "교체 신청 제출", Margin = new Thickness(8, 0, 0, 0), MinWidth = 128, IsEnabled = boardMoves.Count > 0, ToolTip = boardMoves.Count > 0 ? null : "표에서 수업을 맞바꾼 뒤 신청할 수 있습니다." };
        ToolTipService.SetShowOnDisabled(submit, true);
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
        boardSubmit = submit;
        DockPanel.SetDock(submit, Dock.Right); send.Children.Add(submit);
        var reason = new TextBox { MaxLength = 500, Text = boardReason, ToolTip = "신청 사유는 시간표 담당자와 본인에게만 보입니다." };
        System.Windows.Automation.AutomationProperties.SetName(reason, "신청 사유");
        var reasonHint = AdminUi.Label("신청 사유 · 시간표 담당자와 본인에게만 보입니다", "Faint", 12);
        reasonHint.Margin = new Thickness(10, 0, 0, 0); reasonHint.VerticalAlignment = VerticalAlignment.Center; reasonHint.IsHitTestVisible = false;
        reasonHint.Visibility = boardReason.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        reason.TextChanged += (_, _) => { boardReason = reason.Text; boardRequestId = null; reasonHint.Visibility = reason.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        var field = new Grid(); field.Children.Add(reason); field.Children.Add(reasonHint); send.Children.Add(field);
        panel.Children.Add(send);

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        if (boardMoves.Count > 0)
        {
            var edits = new StackPanel { Orientation = Orientation.Horizontal };
            Button Edit(string label, Action change)
            {
                var button = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) }; button.SetResourceReference(StyleProperty, "SubtleButton");
                button.Click += async (_, _) => await Run(async () => { change(); boardRequestId = null; await Board(); });
                return button;
            }
            edits.Children.Add(Edit("마지막 변경 되돌리기", () => boardMoves.RemoveAt(boardMoves.Count - 1)));
            if (boardMoves.Count > 1) edits.Children.Add(Edit("모두 되돌리기", boardMoves.Clear));
            DockPanel.SetDock(edits, Dock.Right); top.Children.Add(edits);
        }
        var heading = AdminUi.Heading($"변경 기록 · {boardMoves.Count}건", 13); heading.VerticalAlignment = VerticalAlignment.Center; top.Children.Add(heading);
        var list = new StackPanel();
        if (boardMoves.Count == 0) list.Children.Add(AdminUi.Label("아직 바꾼 수업이 없습니다. 위 표에서 옮길 수업을 누르세요.", "Muted", 12));
        for (var i = 0; i < boardMoves.Count; i++)
        {
            var line = AdminUi.Label($"{i + 1}. {boardMoves[i].Label}", "Ink", 12.5); line.Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0);
            list.Children.Add(line);
        }
        var record = new StackPanel(); record.Children.Add(top);
        var lines = new ScrollViewer { Content = list, MaxHeight = 78, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        record.Children.Add(lines);
        // The newest change is the one just made, so the list opens at its end.
        lines.Loaded += (_, _) => lines.ScrollToEnd();
        var card = new Border { Child = record, Padding = new Thickness(12, 8, 12, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "Subtle");
        panel.Children.Add(card);
        return panel;
    }

    // The base timetable, one file per grade, and the lessons that move together.
    async Task BaseEditor()
    {
        var rules = await api.Get<TimetableBundleRule[]>("api/timetable/bundle-rules");
        var lessons = await api.Get<TimetableBaseLesson[]>("api/timetable/base-lessons");
        var history = await api.Get<JsonElement[]>("api/timetable/history");
        SchedulePage("기초시간표 · 묶음수업");

        var basePanel = new StackPanel();
        basePanel.Children.Add(AdminUi.Heading("기초시간표", 15));
        var how = AdminUi.Label("나이스에서 학년별로 내려받은 기초시간표 엑셀(.xlsx)을 학년 칸에 올립니다. 한 학년만 다시 올려도 다른 학년 시간표는 그대로 남습니다.", "Muted", 12);
        how.Margin = new Thickness(0, 6, 0, 10); basePanel.Children.Add(how);
        var grades = lessons.Select(l => l.Grade).Concat(setup.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("grade").GetInt32())).Concat([1, 2, 3]).Distinct().Order().ToArray();
        var picks = new SortedDictionary<int, string>();
        var register = new Button { Content = "선택한 파일 등록", IsEnabled = false, ToolTip = "학년 칸에서 파일을 먼저 선택하세요." };
        ToolTipService.SetShowOnDisabled(register, true);
        register.SetResourceReference(StyleProperty, "PrimaryButton");
        var gradeRows = new StackPanel();
        foreach (var grade in grades)
        {
            var rooms = lessons.Where(l => l.Grade == grade).Select(l => l.ClassName).ToHashSet();
            var latest = history.FirstOrDefault(r => r.GetProperty("kind").GetString() == "base" && !r.GetProperty("withdrawn").GetBoolean() &&
                r.GetProperty("classes").EnumerateArray().Any(c => rooms.Contains(c.GetString()!)));
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = AdminUi.Label(grade + "학년", "Ink", 14, true); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var state = AdminUi.Label(rooms.Count == 0 ? "아직 등록되지 않았습니다." : $"{rooms.Count}개 학급 등록됨" +
                (latest.ValueKind == JsonValueKind.Object ? $" · {latest.GetProperty("fileName").GetString()} · 적용 {latest.GetProperty("start").GetString()} ~ {latest.GetProperty("end").GetString()}" : ""),
                rooms.Count == 0 ? "Faint" : "Muted", 12);
            state.TextTrimming = TextTrimming.CharacterEllipsis; state.TextWrapping = TextWrapping.NoWrap; state.ToolTip = state.Text; words.Children.Add(state);
            var picked = AdminUi.Label("", "Blue", 12, true); picked.TextTrimming = TextTrimming.CharacterEllipsis; picked.TextWrapping = TextWrapping.NoWrap; picked.Visibility = Visibility.Collapsed; words.Children.Add(picked);
            Grid.SetColumn(words, 1); row.Children.Add(words);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var clear = new Button { Content = "선택 취소", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 6, 0) }; clear.SetResourceReference(StyleProperty, "SubtleButton");
            var choose = new Button { Content = "파일 선택", MinWidth = 88 };
            System.Windows.Automation.AutomationProperties.SetName(choose, grade + "학년 기초시간표 파일 선택");
            void Show()
            {
                var has = picks.TryGetValue(grade, out var path);
                picked.Text = has ? "올릴 파일 · " + Path.GetFileName(path) : ""; picked.Visibility = clear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                choose.Content = has ? "다른 파일" : "파일 선택";
                register.IsEnabled = picks.Count > 0; register.Content = picks.Count > 0 ? $"선택한 파일 {picks.Count}개 등록" : "선택한 파일 등록";
            }
            choose.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = grade + "학년 기초시간표 파일 선택", Filter = "시간표 엑셀 (*.xlsx)|*.xlsx" };
                if (dialog.ShowDialog(this) == true) { picks[grade] = dialog.FileName; Show(); }
            };
            clear.Click += (_, _) => { picks.Remove(grade); Show(); };
            buttons.Children.Add(clear); buttons.Children.Add(choose); Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
            var line = new Border { Child = row, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
            line.SetResourceReference(Border.BorderBrushProperty, "Line"); gradeRows.Children.Add(line);
        }
        basePanel.Children.Add(gradeRows);
        var baseActions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        register.Click += async (_, _) => await Run(() => Choose([], picks.Select(p => (p.Key, p.Value)).ToArray()));
        var together = new Button { Content = "여러 학년이 한 파일에 있으면 한꺼번에 올리기", ToolTip = "학년을 나누지 않고 파일을 그대로 올립니다." };
        together.SetResourceReference(StyleProperty, "SubtleButton"); together.Click += async (_, _) => await Run(Choose);
        DockPanel.SetDock(together, Dock.Right); baseActions.Children.Add(together);
        register.HorizontalAlignment = HorizontalAlignment.Left; baseActions.Children.Add(register);
        basePanel.Children.Add(baseActions);
        var baseCard = AdminUi.Card(basePanel, 18); baseCard.Margin = new Thickness(0, 4, 0, 12); body.Children.Add(baseCard);

        var bundlePanel = new StackPanel();
        bundlePanel.Children.Add(AdminUi.Heading("묶음수업", 15));
        var about = AdminUi.Label("여러 반이 같은 시간에 나뉘어 듣는 수업을 묶어 두면, 수업을 교체할 때 그 시간의 수업이 모두 함께 움직입니다.", "Muted", 12);
        about.Margin = new Thickness(0, 6, 0, 10); bundlePanel.Children.Add(about);
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
            var chosen = new List<TimetableBundleMember>();
            var add = AdminUi.Group("새 묶음 만들기"); add.Margin = new Thickness(0, 14, 0, 4); bundlePanel.Children.Add(add);
            var guide = AdminUi.Label("학년 → 과목 → 분반 순으로 고릅니다. 과목을 바꿔 가며 여러 과목의 분반을 한 묶음에 담을 수 있습니다.", "Muted", 12);
            guide.Margin = new Thickness(0, 0, 0, 8); bundlePanel.Children.Add(guide);

            // Three lists side by side, each narrowing the next: grade, subject, the classes that run it.
            var panes = new Grid { Height = 248 };
            foreach (var width in new[] { new GridLength(112), new GridLength(10), new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(1.35, GridUnitType.Star) }) panes.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            void Pane(string title, UIElement content, int column)
            {
                var dock = new DockPanel();
                var caption = AdminUi.Label(title, "Muted", 12, true);
                var top = new Border { Child = caption, Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
                top.SetResourceReference(Border.BackgroundProperty, "Subtle"); top.SetResourceReference(Border.BorderBrushProperty, "Line");
                DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); dock.Children.Add(content);
                var frame = new Border { Child = dock, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), ClipToBounds = true };
                frame.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetColumn(frame, column); panes.Children.Add(frame);
            }
            var gradeList = new ListBox { BorderThickness = new Thickness(0) }; System.Windows.Automation.AutomationProperties.SetName(gradeList, "학년");
            var subjectList = new ListBox { BorderThickness = new Thickness(0) }; System.Windows.Automation.AutomationProperties.SetName(subjectList, "과목");
            ScrollViewer.SetHorizontalScrollBarVisibility(subjectList, ScrollBarVisibility.Disabled);
            var classes = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
            Pane("1. 학년", gradeList, 0); Pane("2. 과목", subjectList, 2);
            Pane("3. 분반 · 함께 움직일 반을 체크", new ScrollViewer { Content = classes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 4);
            bundlePanel.Children.Add(panes);

            var picked = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var count = AdminUi.Label("", "Ink", 12, true); count.Margin = new Thickness(0, 10, 0, 2);
            bundlePanel.Children.Add(count); bundlePanel.Children.Add(picked);
            var save = new Button { Content = "묶음으로 저장", HorizontalAlignment = HorizontalAlignment.Left }; save.SetResourceReference(StyleProperty, "PrimaryButton");
            static int Number(string room) { var m = Regex.Match(room, @"(\d+)\s*반"); return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : int.MaxValue; }
            void Classes()
            {
                classes.Children.Clear();
                if ((gradeList.SelectedItem as ListBoxItem)?.Tag is not int level || (subjectList.SelectedItem as ListBoxItem)?.Tag is not string subject)
                { classes.Children.Add(AdminUi.Label("왼쪽에서 과목을 고르면 그 과목을 운영하는 반이 여기에 나옵니다.", "Faint", 12)); return; }
                var found = lessons.Where(l => l.Grade == level && l.Subject == subject).OrderBy(l => Number(l.ClassName)).ThenBy(l => l.ClassName, StringComparer.CurrentCulture).ToArray();
                var boxes = new List<(CheckBox Box, TimetableBundleMember Member)>();
                foreach (var lesson in found)
                {
                    var member = new TimetableBundleMember(lesson.ClassId, lesson.Subject); var used = taken.Contains(member);
                    var box = new CheckBox { Content = $"{lesson.ClassName} · {lesson.Teachers} · 주 {lesson.Hours}시간" + (used ? " · 이미 다른 묶음에 있음" : ""), IsChecked = chosen.Contains(member), IsEnabled = !used, Margin = new Thickness(0, 3, 0, 3) };
                    box.Click += (_, _) => { if (box.IsChecked == true) { if (!chosen.Contains(member)) chosen.Add(member); } else chosen.Remove(member); Picked(); };
                    if (!used) boxes.Add((box, member));
                    classes.Children.Add(box);
                }
                if (boxes.Count > 1)
                {
                    var all = new Button { Content = boxes.All(b => b.Box.IsChecked == true) ? "모두 해제" : "이 과목 반 모두 선택", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 0, 2) };
                    all.SetResourceReference(StyleProperty, "SubtleButton");
                    all.Click += (_, _) =>
                    {
                        var on = !boxes.All(b => b.Box.IsChecked == true);
                        foreach (var (_, member) in boxes) { chosen.Remove(member); if (on) chosen.Add(member); }
                        Picked(); Classes();
                    };
                    classes.Children.Insert(0, all);
                }
            }
            void Picked()
            {
                picked.Children.Clear();
                count.Text = chosen.Count == 0 ? "선택한 수업이 없습니다." : $"선택한 수업 {chosen.Count}개" + (chosen.Count == 1 ? " · 둘 이상 고르면 저장할 수 있습니다." : "");
                foreach (var member in chosen.ToArray())
                {
                    var chip = new Button { Content = names[member] + "  ×", FontSize = 12, FontWeight = FontWeights.Normal, MinHeight = 26, Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 6, 6), ToolTip = "누르면 선택에서 뺍니다." };
                    System.Windows.Automation.AutomationProperties.SetName(chip, names[member] + " 선택에서 빼기");
                    chip.Click += (_, _) => { chosen.Remove(member); Picked(); Classes(); };
                    picked.Children.Add(chip);
                }
                save.IsEnabled = chosen.Count >= 2;
            }
            void Subjects()
            {
                subjectList.Items.Clear();
                if ((gradeList.SelectedItem as ListBoxItem)?.Tag is int level)
                    foreach (var subject in lessons.Where(l => l.Grade == level).GroupBy(l => l.Subject).OrderBy(g => g.Key, StringComparer.CurrentCulture))
                    {
                        var row = new DockPanel();
                        var rooms = AdminUi.Label(subject.Count() + "개 반", "Faint", 12); rooms.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(rooms, Dock.Right); row.Children.Add(rooms);
                        row.Children.Add(new TextBlock { Text = subject.Key, TextTrimming = TextTrimming.CharacterEllipsis });
                        var item = new ListBoxItem { Content = row, Tag = subject.Key, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = subject.Key };
                        System.Windows.Automation.AutomationProperties.SetName(item, subject.Key);
                        subjectList.Items.Add(item);
                    }
                Classes();
            }
            foreach (var level in lessons.Select(l => l.Grade).Distinct().Order())
            {
                var item = new ListBoxItem { Content = level + "학년", Tag = level }; System.Windows.Automation.AutomationProperties.SetName(item, level + "학년"); gradeList.Items.Add(item);
            }
            gradeList.SelectionChanged += (_, _) => Subjects(); subjectList.SelectionChanged += (_, _) => Classes();
            gradeList.SelectedIndex = 0; Picked();
            var name = new TextBox { MaxLength = 60, Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
            System.Windows.Automation.AutomationProperties.SetName(name, "묶음 이름");
            var nameLabel = AdminUi.Label("묶음 이름 (예: 3학년 선택 A)", "Muted", 12); nameLabel.Margin = new Thickness(0, 6, 0, 0);
            bundlePanel.Children.Add(nameLabel); bundlePanel.Children.Add(name);
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
        // Without scrolling: the days on top, every lesson column, and the request controls at the bottom.
        bool Inside(FrameworkElement? part)
        {
            if (part is not { IsVisible: true }) return false;
            var at = part.TransformToAncestor(this).Transform(new Point());
            return at.X >= 0 && at.Y >= 0 && at.X + part.ActualWidth <= ActualWidth + 0.5 && at.Y + part.ActualHeight <= ActualHeight + 0.5;
        }
        if (!Inside(boardSubmit) || !Inside(boardRows) || !boardSubmit!.IsEnabled) throw new InvalidOperationException("교체 신청 버튼이나 시간표가 창 밖으로 나갔습니다.");
        if (boardRows!.ComputedHorizontalScrollBarVisibility == Visibility.Visible) throw new InvalidOperationException("한 주 시간표가 창 너비에 다 들어오지 않습니다.");
        var normal = (Width, Height);
        // The whole school at once is the hardest case for fitting the window.
        boardAll = true; await Board(); UpdateLayout();
        WindowState = WindowState.Maximized; await Task.Delay(400); UpdateLayout();
        var area = SystemParameters.WorkArea;
        if (ActualWidth > area.Width + 2 || ActualHeight > area.Height + 2) throw new InvalidOperationException($"최대화한 창이 작업 표시줄 뒤로 넘어갑니다: {ActualWidth}x{ActualHeight} > {area.Width}x{area.Height}");
        if (!Inside(boardSubmit) || !Inside(boardRows)) throw new InvalidOperationException("최대화하면 교체 신청 버튼이 보이지 않습니다.");
        if (boardRows.ComputedVerticalScrollBarVisibility == Visibility.Visible && boardRows.ViewportHeight >= 48 * 13) throw new InvalidOperationException("모든 선생님이 한 화면에 들어올 수 있는데도 세로 스크롤이 생겼습니다.");
        capture("timetable-board-maximized");
        WindowState = WindowState.Normal; await Task.Delay(200); UpdateLayout();
        if (Math.Abs(Width - normal.Width) > 1 || Math.Abs(Height - normal.Height) > 1) throw new InvalidOperationException("최대화 뒤에 창 크기가 돌아오지 않았습니다.");
        Width = 760; Height = 560; UpdateLayout(); await Task.Delay(100); UpdateLayout();
        if (!Inside(boardSubmit) || !Inside(boardRows)) throw new InvalidOperationException("창을 줄이면 교체 신청 버튼이 보이지 않습니다.");
        capture("timetable-board-small");
        Width = normal.Width; Height = normal.Height; UpdateLayout();
        boardReason = "검증용 교체";
        boardRequestId = NewScheduleId();
        await api.Send<TimetableSwap>(HttpMethod.Post, "api/timetable/swaps", new TimetableSwapCreate(boardRequestId, boardPlan.Revision, BoardExchanges!, boardDependencies, boardReason));
        boardMoves.Clear();
        await SwapQueue(); UpdateLayout(); capture("timetable-swap-queue");
        await BaseEditor(); UpdateLayout();
        // Grade, then subject, then the classes that run it: two ticks make a bundle that can be saved.
        static IEnumerable<T> Under<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T found) yield return found;
                foreach (var deeper in Under<T>(child)) yield return deeper;
            }
        }
        var subjects = Under<ListBox>(body).Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "과목");
        if (subjects.Items.Count == 0) throw new InvalidOperationException("학년을 골라도 과목이 나오지 않습니다.");
        subjects.SelectedIndex = 0; UpdateLayout();
        var rooms = Under<CheckBox>(body).Where(box => box.IsEnabled && box.Content is string label && label.Contains("주 ")).Take(2).ToArray();
        if (rooms.Length < 2) throw new InvalidOperationException("과목을 골라도 분반이 나오지 않습니다.");
        foreach (var room in rooms) { room.IsChecked = true; room.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
        UpdateLayout();
        var save = Under<Button>(body).Single(button => Equals(button.Content, "묶음으로 저장"));
        if (!save.IsEnabled || Under<Button>(body).Count(button => button.Content is string chip && chip.EndsWith("×", StringComparison.Ordinal)) != 2) throw new InvalidOperationException("고른 분반이 선택 목록에 담기지 않았습니다.");
        if (Under<Button>(body).Count(button => Equals(button.Content, "파일 선택")) < 3) throw new InvalidOperationException("학년별 기초시간표 칸이 보이지 않습니다.");
        capture("timetable-base"); pageScroll.ScrollToEnd(); UpdateLayout(); capture("timetable-bundles");
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
