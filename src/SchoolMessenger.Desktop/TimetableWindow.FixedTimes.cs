using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

// 교체하지 않는 시간: weekly times (creative activities and the like) the timetable manager keeps out of teachers' trades.
// Each change is saved at once, like the swap range beside it.
public sealed partial class TimetableWindow
{
    static readonly string[] FixedWeekdays = ["월", "화", "수", "목", "금"];
    static string FixedLabel(TimetableFixedTime t) => $"{FixedWeekdays[t.Weekday - 1]}요일 {t.Period}교시 · {(t.Grade == 0 ? "모든 학년" : t.Grade + "학년")}";

    sealed record FixedParts(Window Window, ComboBox Day, ComboBox Period, ComboBox Grade, Button Add, StackPanel List, TextBlock Note);

    async Task FixedTimesEditor()
    {
        (await BuildFixedTimes()).Window.ShowDialog();
        setup = await api.Get<JsonElement>("api/timetable/setup");
        await SwapQueue();
    }

    async Task<FixedParts> BuildFixedTimes()
    {
        var times = await api.Get<TimetableFixedTime[]>("api/timetable/fixed-times");
        var grades = setup.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("grade").GetInt32()).Where(g => g > 0).Distinct().Order().ToArray();
        var window = new Window { Owner = this, Title = "교체하지 않는 시간", Width = 480, Height = Math.Min(560, SystemParameters.WorkArea.Height - 40), WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource("SchoolWindow") };
        window.SetResourceReference(BackgroundProperty, "Soft");
        var page = new DockPanel { Margin = new Thickness(22, 18, 22, 18) }; window.Content = page;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); page.Children.Add(top);
        top.Children.Add(AdminUi.PageTitle("교체하지 않는 시간", "창의적 체험활동처럼 매주 같은 시간의 수업은 선생님들이 교체 신청으로 옮기거나 그 시간으로 넣을 수 없습니다. 시간표 담당자는 바꿀 수 있습니다."));

        var form = new WrapPanel();
        ComboBox Pick(string name, string[] items, double width)
        {
            var box = new ComboBox { ItemsSource = items, SelectedIndex = 0, Width = width, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(box, name); form.Children.Add(box); return box;
        }
        var day = Pick("요일", FixedWeekdays.Select(d => d + "요일").ToArray(), 84);
        var period = Pick("교시", Enumerable.Range(1, 9).Select(p => p + "교시").ToArray(), 76);
        var grade = Pick("학년", ["모든 학년", .. grades.Select(g => g + "학년")], 104);
        var add = new Button { Content = "추가", MinWidth = 70, VerticalAlignment = VerticalAlignment.Center };
        add.SetResourceReference(StyleProperty, "PrimaryButton"); form.Children.Add(add);
        top.Children.Add(AdminUi.Card(form, 14));
        var note = AdminUi.Label("", "Muted", 12); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 10, 0, 6); top.Children.Add(note);
        var list = new StackPanel();
        page.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        async Task Save(TimetableFixedTime[] next, string done)
        {
            try { times = await api.Send<TimetableFixedTime[]>(HttpMethod.Put, "api/timetable/fixed-times", next); Draw(); note.Text = done; }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException) { note.Text = error.Message; }
        }
        void Draw()
        {
            list.Children.Clear();
            if (times.Length == 0) { list.Children.Add(AdminUi.Empty("등록한 시간이 없습니다", "위에서 요일·교시·학년을 고르고 ‘추가’를 누르세요.")); return; }
            foreach (var time in times)
            {
                var row = new DockPanel();
                var remove = new Button { Content = "삭제", Padding = new Thickness(12, 3, 12, 3) }; remove.SetResourceReference(StyleProperty, "SubtleButton");
                AutomationProperties.SetName(remove, FixedLabel(time) + " 삭제");
                DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var label = AdminUi.Label(FixedLabel(time), "Ink", 13); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
                remove.Click += async (_, _) => await Save(times.Where(t => t != time).ToArray(), FixedLabel(time) + "을(를) 지웠습니다. 이제 선생님들이 이 시간 수업을 바꿀 수 있습니다.");
                var card = AdminUi.Card(row, 10); card.Margin = new Thickness(0, 0, 0, 6); list.Children.Add(card);
            }
        }
        add.Click += async (_, _) =>
        {
            var time = new TimetableFixedTime(day.SelectedIndex + 1, period.SelectedIndex + 1, grade.SelectedIndex == 0 ? 0 : grades[grade.SelectedIndex - 1]);
            if (times.Contains(time)) { note.Text = FixedLabel(time) + "은(는) 이미 있습니다."; return; }
            await Save([.. times, time], FixedLabel(time) + "을(를) 추가했습니다. 매주 이 시간 수업은 시간표 담당자만 바꿀 수 있습니다.");
        };
        Draw();
        return new(window, day, period, grade, add, list, note);
    }

    // The manager adds a weekly time, the board shades it, and taking it away again frees it.
    async Task VerifyFixedTimes(TimetablePlanSlot lesson, Action<string> capture, Action<Window, string> captureWindow)
    {
        var date = TimetableDate(lesson.Position.Date);
        var parts = await BuildFixedTimes(); parts.Window.Show();
        parts.Day.SelectedIndex = ((int)date.DayOfWeek + 6) % 7; parts.Period.SelectedIndex = lesson.Position.Period - 1;
        parts.Add.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        for (var i = 0; i < 50 && parts.Note.Text.Length == 0; i++) await Task.Delay(100);
        parts.Window.UpdateLayout();
        if (!parts.Note.Text.Contains("추가했습니다")) throw new InvalidOperationException("교체하지 않는 시간을 추가하지 못했습니다: " + parts.Note.Text);
        captureWindow(parts.Window, "timetable-fixed-times");
        parts.Window.Close();
        boardWeek = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); await Board(); UpdateLayout();
        if (!boardCells.TryGetValue(lesson.Position, out var cell) || (string)cell.Tag != "fixed") throw new InvalidOperationException("교체하지 않는 시간이 교체 표에 표시되지 않았습니다.");
        capture("timetable-board-fixed");
        await api.Send<TimetableFixedTime[]>(HttpMethod.Put, "api/timetable/fixed-times", Array.Empty<TimetableFixedTime>());
        await Board();
        if (boardPlan!.Slots.Any(s => s.Fixed)) throw new InvalidOperationException("교체하지 않는 시간을 지웠는데 표에 남아 있습니다.");
    }
}
