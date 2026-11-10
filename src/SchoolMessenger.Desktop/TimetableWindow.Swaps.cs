using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class TimetableWindow
{
    string[] publishingSwaps = [];
    TimetableDay[] printDays = [];
    string printTitle = "";
    static string NewScheduleId() => Guid.NewGuid().ToString("N");
    bool CanManageSchedule => setup.GetProperty("canManage").GetBoolean();
    string? ScheduleUser => api.Session?.User?.Id;

    void AddScheduleActions(bool hasTimetable)
    {
        var tools = new WrapPanel();
        void Add(string label, Func<Task> action, string? style = "SubtleButton")
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 4) };
            if (style is not null) button.SetResourceReference(StyleProperty, style);
            button.Click += async (_, _) => await Run(action);
            tools.Children.Add(button);
        }
        Add("수업 교체 신청", SwapEditor, hasTimetable ? "PrimaryButton" : null);
        Add("교체 신청 현황", SwapQueue, null);
        if (CanManageSchedule) { Add("기초시간표 · 묶음수업", BaseEditor); Add("시간표 생성기", OpenGenerator); }
        if (printDays.Any(day => day.PublishedAt > 0))
            Add("게시 시간표 인쇄", () => { PrintTimetable(); return Task.CompletedTask; });
        scheduleFooter.Child=tools;
        scheduleFooter.Visibility=Visibility.Visible;
    }

    void SchedulePage(string title)
    {
        ResetBody();
        Action("시간표로 돌아가기", Load);
        body.Children.Add(AdminUi.Heading(title, 22));
        body.Children.Add(feedback);
        pageScroll.ScrollToTop();
    }

    static string StatusLabel(string status) => status switch
    {
        "pending" => "승인 대기",
        "approved" => "승인·게시 완료",
        "rejected" => "반려",
        "cancelled" => "취소",
        _ => "재검토 필요"
    };

    static string SwapLine(TimetableSwapChange change) =>
        $"{change.Position.Date} · {change.Before.ClassName} {change.Position.Period}교시  " +
        $"{change.Before.Subject} ({change.Before.TeacherName}) → {change.After.Subject} ({change.After.TeacherName})";

    static string SwapSummary(TimetableSwap swap) =>
        $"{swap.OwnerName} · {StatusLabel(swap.Status)} · {SwapLine(swap.Changes[0])}" +
        (swap.Changes.Length > 1 ? $" 외 {swap.Changes.Length - 1}칸" : "");

    async Task SwapQueue()
    {
        var rows = await api.Get<TimetableSwap[]>("api/timetable/swaps");
        var byId = rows.ToDictionary(s => s.Id);
        SchedulePage("교체 신청 현황");
        // Page tools sit on one line under the heading instead of stacking full-width rows.
        var tools = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        body.Children.Add(tools);
        Button Tool(string label, Func<Task> action) { var b = AdminUi.Tone(new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) }, label); if (label.Contains("승인")) b.SetResourceReference(StyleProperty, "PrimaryButton"); b.Click += async (_, _) => await Run(action); tools.Children.Add(b); return b; }
        Tool("새로고침", SwapQueue);
        var selectedRequests = new Dictionary<string, CheckBox>();
        if (CanManageSchedule && rows.Any(s => s.Status == "pending"))
        {
            string[] Picked() => selectedRequests.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray() is { Length: > 0 } ids
                ? ids : throw new InvalidOperationException("승인할 신청을 체크하세요.");
            Tool("선택 신청 승인·게시", async () =>
            {
                var ids = Picked();
                if (Notice.Show(this, $"교체 신청 {ids.Length}건을 승인해 시간표에 반영할까요?\n관련 선생님과 담임 선생님께 알림이 갑니다.", "교체 신청 승인", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await api.Send<JsonElement>(HttpMethod.Post, "api/timetable/swaps/approve", new TimetableCommit("", NewScheduleId(), ids));
                await SwapQueue();
                feedback.Text = $"교체 신청 {ids.Length}건을 승인해 시간표에 반영했습니다.";
            });
            Tool("나이스 파일과 대조해 게시", async () => await Choose(Picked()));
        }
        foreach (var swap in rows)
        {
            var panel = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            if (CanManageSchedule && swap.Status == "pending")
            {
                var pick = new CheckBox { Content = swap.OwnerName, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                selectedRequests.Add(swap.Id, pick);
                head.Children.Add(pick);
            }
            else head.Children.Add(AdminUi.Label(swap.OwnerName, "Ink", 14, true));
            var badge = AdminUi.Badge(StatusLabel(swap.Status), swap.Status switch { "approved" => "Success", "rejected" => "Danger", "cancelled" => "", _ => "Warning" });
            badge.Margin = new Thickness(10, 0, 0, 0); head.Children.Add(badge);
            panel.Children.Add(head);
            var decisions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var change in swap.Changes) panel.Children.Add(Text(SwapLine(change), 12));
            foreach (var dependency in swap.Dependencies)
            {
                var label = byId.TryGetValue(dependency, out var preceding) ? SwapSummary(preceding) : "선행 신청 " + dependency[..8];
                panel.Children.Add(Text("선행 · " + label, 12));
            }
            if (swap.Reason.Length > 0) panel.Children.Add(Text("사유 · " + swap.Reason, 12));
            if (swap.ReviewedAt is { } reviewed)
                panel.Children.Add(Text($"{swap.ReviewerName} · {DateTimeOffset.FromUnixTimeMilliseconds(reviewed).ToOffset(SchoolDay.KoreaOffset):yyyy-MM-dd HH:mm}", 12));
            if (swap.Status is "pending" or "review")
            {
                if (swap.Owner == ScheduleUser) Decision("신청 취소", "cancelled");
                if (CanManageSchedule) Decision("반려", "rejected");
            }
            void Decision(string label, string status)
            {
                var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 0) };
                button.SetResourceReference(StyleProperty, "DangerButton");
                button.Click += async (_, _) => await Run(async () =>
                {
                    if (Notice.Show(this, $"{label}할까요?", "교체 신청", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    await api.Send<TimetableSwap>(HttpMethod.Post, $"api/timetable/swaps/{swap.Id}/decision", new TimetableSwapDecision(status));
                    await SwapQueue();
                });
                decisions.Children.Add(button);
            }
            if (decisions.Children.Count > 0) panel.Children.Add(decisions);
            var card = AdminUi.Card(panel, 14);
            card.Margin = new Thickness(0, 6, 0, 6);
            body.Children.Add(card);
        }
        if (rows.Length == 0) body.Children.Add(AdminUi.Empty("아직 교체 신청이 없습니다", "수업 교체를 신청하면 승인 상태가 여기에 표시됩니다."));
    }

    void PrintTimetable()
    {
        var days = printDays.Where(d => printDays.Length == 1 || d.Slots.Any(s => s.Subject.Length > 0) ||
            TimetableDate(d.Date).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToArray();
        var document = new FlowDocument { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 11,
            PagePadding = new Thickness(24), PageWidth = 1040, ColumnWidth = 1040, Foreground = Brushes.Black };
        document.Blocks.Add(new Paragraph(new Run(printTitle)) { FontSize = 20, FontWeight = FontWeights.Bold });
        document.Blocks.Add(new Paragraph(new Run($"{days[0].Date} ~ {days[^1].Date} · 게시 시간표")));
        var table = new Table { CellSpacing = 0 };
        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);
        TableRow Row(IEnumerable<string> values, bool header)
        {
            var row = new TableRow();
            foreach (var value in values) row.Cells.Add(new TableCell(new Paragraph(new Run(value)) { Margin = new Thickness(0) })
            {
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5), Padding = new Thickness(5),
                Background = header ? Brushes.WhiteSmoke : Brushes.White
            });
            return row;
        }
        table.Columns.Add(new TableColumn { Width = new GridLength(58) });
        foreach (var _ in days) table.Columns.Add(new TableColumn());
        rows.Rows.Add(Row(new[] { "교시" }.Concat(days.Select(d => TimetableDate(d.Date).ToString("M.d (ddd)", Korean))), true));
        var lastPeriod = days.SelectMany(d => d.Slots).Where(s => s.Subject.Length > 0).Select(s => s.Period).DefaultIfEmpty(7).Max();
        for (var period = 1; period <= lastPeriod; period++)
        {
            var number = period;
            rows.Rows.Add(Row(new[] { $"{number}교시" }.Concat(days.Select(day => string.Join("\n", day.Slots
                .Where(s => s.Period == number && s.Subject.Length > 0)
                .Select(s => $"{s.Subject}\n{s.ClassName} · {s.TeacherName}")))), false));
        }
        document.Blocks.Add(table);
        var preview = new Window { Owner = this, Title = "시간표 인쇄 미리보기", Width = 1040, Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Style=(Style)Application.Current.FindResource("SchoolWindow") };
        var layout = new DockPanel();
        var print = new Button { Content = "인쇄…", Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(print, Dock.Bottom);
        layout.Children.Add(print);
        layout.Children.Add(new FlowDocumentScrollViewer { Document = document });
        print.Click += (_, _) =>
        {
            try
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return;
                var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
                document.PageWidth = dialog.PrintableAreaWidth;
                document.ColumnWidth = dialog.PrintableAreaWidth;
                paginator.PageSize = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
                dialog.PrintDocument(paginator, printTitle);
            }
            catch (Exception error) { Notice.Show(preview, error.Message, "시간표 인쇄"); }
        };
        preview.Content = layout;
        preview.ShowDialog();
    }

    static DateTime TimetableDate(string date) => DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
