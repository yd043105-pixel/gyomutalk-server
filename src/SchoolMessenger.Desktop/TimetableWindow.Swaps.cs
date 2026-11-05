using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
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

    void AddScheduleActions()
    {
        Action("수업 교체 신청", SwapEditor);
        Action("교체 신청 현황", SwapQueue);
        if (CanManageSchedule) Action("묶음수업 설정", BundleEditor);
        if (printDays.Any(day => day.PublishedAt > 0))
            Action("게시 시간표 인쇄", () => { PrintTimetable(); return Task.CompletedTask; });
    }

    void SchedulePage(string title)
    {
        body.Children.Clear();
        body.Children.Add(AdminUi.Heading(title, 22));
        body.Children.Add(feedback);
        Action("시간표로 돌아가기", Load);
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

    sealed record ScheduleOption(TimetablePlanSlot Slot, string Label)
    {
        public override string ToString() => Label;
    }

    static string OptionLabel(TimetablePlanSlot row, HashSet<TimetablePosition> waiting) =>
        $"{row.Lesson.ClassName} {row.Position.Period}교시 · {row.Lesson.Subject} ({row.Lesson.TeacherName})" +
        (row.Mine ? " · 내 수업" : "") +
        (row.BundleName is null ? "" : $" · 묶음 {row.BundleName}") +
        (waiting.Contains(row.Position) ? " · 신청 있음" : "");

    async Task SwapEditor()
    {
        var requests = await api.Get<TimetableSwap[]>("api/timetable/swaps");
        SchedulePage("수업 교체 신청");
        body.Children.Add(Text("같은 학급의 두 수업을 교환합니다. 묶음수업은 구성 수업을 모두 추가하세요."));
        var firstDate = new DatePicker { SelectedDate = selected, Width = 165 };
        var secondDate = new DatePicker { SelectedDate = selected, Width = 165 };
        var dateFields = new WrapPanel();
        dateFields.Children.Add(firstDate);
        dateFields.Children.Add(secondDate);
        body.Children.Add(dateFields);
        var include = new CheckBox { Content = "대기 중 신청을 가정해 찾기", Margin = new Thickness(0, 10, 0, 8) };
        body.Children.Add(include);
        var dependencies = new Dictionary<string, CheckBox>();
        var pending = requests.Where(s => s.Status is "pending" or "review").ToArray();
        if (pending.Length > 0)
        {
            var pendingList = new StackPanel();
            var expander = new Expander { Header = $"대기·재검토 신청 {pending.Length}건", Content = pendingList, IsExpanded = true };
            body.Children.Add(expander);
            foreach (var request in pending)
            {
                var option = new CheckBox { Content = Text(SwapSummary(request), 12), IsEnabled = false, Margin = new Thickness(0, 4, 0, 4) };
                option.ToolTip = string.Join("\n", request.Changes.Select(SwapLine));
                pendingList.Children.Add(option);
                if (request.Status == "pending") dependencies[request.Id] = option;
            }
        }

        var waiting = pending.SelectMany(s => s.Changes.Select(c => c.Position)).ToHashSet();
        var pairs = new List<TimetableExchange>();
        var added = new StackPanel();
        var first = new ComboBox { MinWidth = 350, Margin = new Thickness(0, 4, 0, 4) };
        var second = new ComboBox { MinWidth = 350, Margin = new Thickness(0, 4, 0, 4) };
        var reason = new TextBox { MaxLength = 500, Margin = new Thickness(0, 6, 0, 10) };
        TimetableSwapPlan? plan = null;
        string[] planDependencies = [];
        ScheduleOption[] options = [];
        string? requestId = null;
        void Reset()
        {
            plan = null;
            first.ItemsSource = null;
            second.ItemsSource = null;
            added.Children.Clear();
            pairs.Clear();
            requestId = null;
        }
        firstDate.SelectedDateChanged += (_, _) => Reset();
        secondDate.SelectedDateChanged += (_, _) => Reset();
        include.Checked += (_, _) => { foreach (var item in dependencies.Values) item.IsEnabled = true; Reset(); };
        include.Unchecked += (_, _) => { foreach (var item in dependencies.Values) { item.IsChecked = false; item.IsEnabled = false; } Reset(); };
        foreach (var item in dependencies.Values)
        {
            item.Checked += (_, _) => Reset();
            item.Unchecked += (_, _) => Reset();
        }

        Action("선택한 날짜의 수업 불러오기", async () =>
        {
            Reset();
            if (firstDate.SelectedDate is not { } from || secondDate.SelectedDate is not { } to)
                throw new InvalidOperationException("두 날짜를 선택하세요.");
            var source = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var target = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var chosen = dependencies.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToHashSet();
            foreach (var id in chosen.ToArray()) AddPreceding(id);
            void AddPreceding(string id)
            {
                foreach (var before in requests.Single(s => s.Id == id).Dependencies)
                    if (dependencies.ContainsKey(before) && chosen.Add(before)) AddPreceding(before);
            }
            planDependencies = chosen.Order(StringComparer.Ordinal).ToArray();
            plan = await api.Send<TimetableSwapPlan>(HttpMethod.Post, "api/timetable/swap-plan",
                new TimetableSwapPlanRequest([source, target], planDependencies));
            options = plan.Slots.Where(s => s.Position.Date == target).Select(s => new ScheduleOption(s, OptionLabel(s, waiting))).ToArray();
            first.ItemsSource = plan.Slots.Where(s => s.Position.Date == source)
                .OrderByDescending(s => s.Mine).Select(s => new ScheduleOption(s, OptionLabel(s, waiting))).ToArray();
            feedback.Text = planDependencies.Length == 0 ? "공식 시간표 기준입니다." : $"선행 신청 {planDependencies.Length}건이 반영된다고 가정한 시간표입니다.";
        });

        first.SelectionChanged += (_, _) =>
        {
            second.ItemsSource = first.SelectedItem is ScheduleOption item
                ? options.Where(o => o.Slot.Position.ClassId == item.Slot.Position.ClassId && o.Slot.Position != item.Slot.Position).ToArray()
                : [];
        };
        body.Children.Add(Text("출발 날짜의 수업", 12));
        body.Children.Add(first);
        body.Children.Add(Text("같은 학급의 교환 수업", 12));
        body.Children.Add(second);
        Action("교환 쌍 추가", () =>
        {
            if (plan is null || first.SelectedItem is not ScheduleOption a || second.SelectedItem is not ScheduleOption b)
                throw new InvalidOperationException("두 수업을 선택하세요.");
            if (pairs.Count >= 12 || pairs.Any(p => p.First == a.Slot.Position || p.Second == a.Slot.Position ||
                    p.First == b.Slot.Position || p.Second == b.Slot.Position))
                throw new InvalidOperationException("같은 수업은 한 번만 선택할 수 있고 교환 쌍은 최대 12개입니다.");
            var pair = new TimetableExchange(a.Slot.Position, b.Slot.Position);
            pairs.Add(pair);
            requestId = null;
            var row = new DockPanel();
            var remove = new Button { Content = "빼기", Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(Text($"{pair.First.Date} {a.Label} ↔ {pair.Second.Date} {b.Label}", 12));
            remove.Click += (_, _) => { pairs.Remove(pair); added.Children.Remove(row); requestId = null; };
            added.Children.Add(row);
            return Task.CompletedTask;
        });
        body.Children.Add(added);
        body.Children.Add(Text("신청 사유 (수업계와 본인에게 표시)", 12));
        reason.TextChanged += (_, _) => requestId = null;
        body.Children.Add(reason);
        Action("교체 신청 제출", async () =>
        {
            if (plan is null || pairs.Count == 0) throw new InvalidOperationException("교환할 수업을 추가하세요.");
            requestId ??= NewScheduleId();
            await api.Send<TimetableSwap>(HttpMethod.Post, "api/timetable/swaps",
                new TimetableSwapCreate(requestId, plan.Revision, pairs.ToArray(), planDependencies, reason.Text));
            await SwapQueue();
            feedback.Text = "교체 신청을 접수했습니다.";
        });
    }

    async Task SwapQueue()
    {
        var rows = await api.Get<TimetableSwap[]>("api/timetable/swaps");
        var byId = rows.ToDictionary(s => s.Id);
        SchedulePage("교체 신청 현황");
        Action("새로 고침", SwapQueue);
        var selectedRequests = new Dictionary<string, CheckBox>();
        foreach (var swap in rows)
        {
            var panel = new StackPanel();
            var heading = $"{swap.OwnerName} · {StatusLabel(swap.Status)}";
            if (CanManageSchedule && swap.Status == "pending")
            {
                var pick = new CheckBox { Content = heading };
                selectedRequests.Add(swap.Id, pick);
                panel.Children.Add(pick);
            }
            else panel.Children.Add(AdminUi.Label(heading, "Ink", 14, true));
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
                var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += async (_, _) => await Run(async () =>
                {
                    if (MessageBox.Show(this, $"{label}할까요?", "교체 신청", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    await api.Send<TimetableSwap>(HttpMethod.Post, $"api/timetable/swaps/{swap.Id}/decision", new TimetableSwapDecision(status));
                    await SwapQueue();
                });
                panel.Children.Add(button);
            }
            var card = AdminUi.Card(panel, 12);
            card.Margin = new Thickness(0, 6, 0, 6);
            body.Children.Add(card);
        }
        if (rows.Length == 0) body.Children.Add(Text("아직 교체 신청이 없습니다."));
        if (selectedRequests.Count > 0) Action("선택 신청 승인·나이스 파일 선택", async () =>
        {
            var ids = selectedRequests.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToArray();
            if (ids.Length == 0) throw new InvalidOperationException("파일에 반영한 신청을 선택하세요.");
            await Choose(ids);
        });
    }

    async Task BundleEditor()
    {
        var bundles = await api.Get<TimetableBundle[]>("api/timetable/bundles");
        SchedulePage("묶음수업 설정");
        var date = new DatePicker { SelectedDate = selected, Width = 165, HorizontalAlignment = HorizontalAlignment.Left };
        var name = new TextBox { MaxLength = 60 };
        var kind = new ComboBox { ItemsSource = new[] { new Choice("consecutive", "연강 · 같은 학급의 연속 교시"), new Choice("simultaneous", "동시 수업 · 같은 시간에 시작·종료") }, SelectedIndex = 0 };
        var lessons = new ListBox { SelectionMode = SelectionMode.Multiple, MaxHeight = 260 };
        body.Children.Add(Text("날짜를 골라 함께 움직일 수업을 선택하세요.", 12));
        body.Children.Add(date);
        date.SelectedDateChanged += (_, _) => lessons.ItemsSource = null;
        Action("날짜의 수업 불러오기", async () =>
        {
            if (date.SelectedDate is not { } day) throw new InvalidOperationException("날짜를 선택하세요.");
            var plan = await api.Send<TimetableSwapPlan>(HttpMethod.Post, "api/timetable/swap-plan",
                new TimetableSwapPlanRequest([day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], []));
            lessons.ItemsSource = plan.Slots.Select(s => new ScheduleOption(s, OptionLabel(s, []))).ToArray();
        });
        body.Children.Add(lessons);
        body.Children.Add(Text("묶음 이름", 12));
        body.Children.Add(name);
        body.Children.Add(kind);
        Action("선택 수업을 묶음으로 저장", async () =>
        {
            var slots = lessons.SelectedItems.Cast<ScheduleOption>().Select(s => s.Slot.Position).ToArray();
            await api.Send<TimetableBundle>(HttpMethod.Post, "api/timetable/bundles",
                new TimetableBundle(NewScheduleId(), name.Text, ((Choice)kind.SelectedItem).Id, slots));
            await BundleEditor();
        });
        foreach (var bundle in bundles.OrderBy(b => b.Slots[0].Date))
        {
            var classes = setup.GetProperty("classes").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("name").GetString());
            var list = string.Join(", ", bundle.Slots.Select(p => $"{p.Date} {classes.GetValueOrDefault(p.ClassId)} {p.Period}교시"));
            body.Children.Add(Text($"{bundle.Name} · {(bundle.Kind == "consecutive" ? "연강" : "동시 수업")}\n{list}", 12));
            Action(bundle.Name + " 삭제", async () =>
            {
                if (MessageBox.Show(this, $"‘{bundle.Name}’ 묶음을 삭제할까요?", "묶음수업", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                await api.Send<object>(HttpMethod.Delete, "api/timetable/bundles/" + bundle.Id);
                await BundleEditor();
            });
        }
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
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
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
            catch (Exception error) { MessageBox.Show(preview, error.Message, "시간표 인쇄"); }
        };
        preview.Content = layout;
        preview.ShowDialog();
    }

    static DateTime TimetableDate(string date) => DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
