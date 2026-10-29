using System.Text.Json;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Data;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace SchoolMessenger.Desktop;

public sealed class WorkWindow : Window
{
    readonly MainWindow main;
    readonly Api api;
    readonly ListBox list = new();
    readonly Button todosTab,submissionsTab,directAdd;
    readonly StackPanel detail = new() { Margin = new Thickness(24) };
    readonly TextBlock feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 8, 16, 10) };
    readonly Dictionary<string, SubmissionPacket> pending = new();
    WrapPanel? targetsPanel;
    string view = "todos";
    int offset;
    bool busy, rendering;
    readonly DispatcherTimer deadlineClock=new(){Interval=TimeSpan.FromSeconds(30)};
    DateOnly displayedDay=KoreanToday;
    static DateOnly KoreanToday=>DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).DateTime);
    string? requestedId;
    public WorkWindow(MainWindow main, string? requestId = null)
    {
        this.main = main; api = main.Api!; Owner = main; Resources = main.Resources;
        Title = "교무톡 · 할 일과 파일 제출"; Width = 860; Height = 650; MinWidth = 650; MinHeight = 500;
        Style = (Style)FindResource("SchoolWindow"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        requestedId = requestId; if (requestId is not null) view = "submission-requests";
        var layout = new DockPanel();layout.SetResourceReference(Panel.BackgroundProperty,"Surface"); Content = layout;
        var heading = new StackPanel { Margin = new Thickness(20, 16, 20, 12) };
        heading.Children.Add(Text("할 일과 파일 제출", 18)); heading.Children.Add(Text("진행 중인 일은 기한순으로 표시합니다. D-Day는 한국 날짜 기준입니다.", 11));
        var navigation = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        todosTab=Action("나의 할 일", async () => { view = "todos"; offset = 0; detail.Children.Clear(); await Reload(); });navigation.Children.Add(todosTab);
        submissionsTab=Action("파일 제출 · 수합", async () => { view = "submission-requests"; offset = 0; detail.Children.Clear(); await Reload(); });navigation.Children.Add(submissionsTab);
        navigation.Children.Add(Action("새로고침", Reload));
        directAdd=Action("할 일 직접 추가", async () => { view="todos";offset=0;await Reload();Todo(null); });navigation.Children.Add(directAdd);
        navigation.Children.Add(Action("다음 목록", async () => { offset += 100; detail.Children.Clear(); await Reload(); }));
        heading.Children.Add(navigation); DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        DockPanel.SetDock(feedback, Dock.Bottom); layout.Children.Add(feedback);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        list.BorderThickness = new Thickness(0, 0, 1, 0); list.BorderBrush = (Brush)FindResource("Line");
        list.SetResourceReference(Control.BackgroundProperty,"Soft");
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);list.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        var row=new FrameworkElementFactory(typeof(StackPanel));row.SetBinding(ToolTipService.ToolTipProperty,new Binding("Label"));
        var title=new FrameworkElementFactory(typeof(TextBlock));title.SetBinding(TextBlock.TextProperty,new Binding("Title"));title.SetValue(TextBlock.FontSizeProperty,12d);title.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);title.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);title.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);row.AppendChild(title);
        var summary=new FrameworkElementFactory(typeof(TextBlock));summary.SetBinding(TextBlock.TextProperty,new Binding("Summary"));summary.SetValue(TextBlock.FontSizeProperty,10d);summary.SetResourceReference(TextBlock.ForegroundProperty,"Muted");summary.SetValue(FrameworkElement.MarginProperty,new Thickness(0,6,0,0));summary.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);summary.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);row.AppendChild(summary);
        list.ItemTemplate=new DataTemplate{VisualTree=row};
        list.SelectionChanged += async (_, _) => { if (!rendering && list.SelectedItem is WorkRow row) await Run(() => Show(row.Value)); };
        grid.Children.Add(list); var scroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); layout.Children.Add(grid);
        Loaded += async (_, _) => await Run(async () => { await Reload(); if (requestedId is not null) await Submission(requestedId); });
        Loaded+=(_,_)=>deadlineClock.Start();Closed+=(_,_)=>deadlineClock.Stop();
        deadlineClock.Tick+=async(_,_)=>{var today=KoreanToday;if(today!=displayedDay&&!busy){if(view=="todos")await Run(Reload);else displayedDay=today;}};
        Closing += (_, e) => { if (busy) e.Cancel = true; };
    }
    static TextBlock Text(string text, double size = 13)
    {var block=new TextBlock{Text=text,FontSize=size,FontWeight=size>=16?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)};block.SetResourceReference(TextBlock.ForegroundProperty,size<=12?"Muted":"Ink");return block;}
    Border Card(UIElement content,string background="Soft")
    {
        var card=new Border{Child=content,Padding=new Thickness(12),CornerRadius=new CornerRadius(12),BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,10)};
        card.SetResourceReference(Border.BackgroundProperty,background);card.SetResourceReference(Border.BorderBrushProperty,"Line");return card;
    }
    Button Action(string label, Func<Task> action,bool primary=false)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), FontSize = 11 };
        if(primary)button.SetResourceReference(StyleProperty,"PrimaryButton");
        button.Click += async (_, _) => { if (busy) return; busy = true; button.IsEnabled = false; try { await Run(action); } finally { busy = false; button.IsEnabled = true; } }; return button;
    }
    async Task Run(Func<Task> action) { feedback.Text = ""; try { await action(); } catch (Exception error) { feedback.Text = error.Message; } }
    public async Task Reload()
    {
        var currentView = view;
        todosTab.SetResourceReference(Control.BackgroundProperty,currentView=="todos"?"Pale":"Surface");todosTab.SetResourceReference(Control.ForegroundProperty,currentView=="todos"?"Blue":"Muted");
        submissionsTab.SetResourceReference(Control.BackgroundProperty,currentView=="submission-requests"?"Pale":"Surface");submissionsTab.SetResourceReference(Control.ForegroundProperty,currentView=="submission-requests"?"Blue":"Muted");
        var previous = (list.SelectedItem as WorkRow)?.Value.GetProperty("id").GetString();
        var rows = await api.Get<JsonElement[]>($"api/{view}?offset={offset}");
        if (currentView != view) return;
        feedback.Text="";
        rendering = true;
        try { displayedDay=KoreanToday;list.ItemsSource = rows.Select(v => new WorkRow(v, v.GetProperty("title").GetString()!.ReplaceLineEndings(" "), currentView == "todos" ? Status(v.GetProperty("status").GetString()!) + " · " + Deadline(String(v,"dueDate"),String(v,"status"),displayedDay) : v.GetProperty("ownerName").GetString() + " · " + v.GetProperty("submittedCount").GetInt32() + "/" + v.GetProperty("targetCount").GetInt32() + "명 제출")).ToArray(); list.SelectedItem = list.Items.Cast<WorkRow>().FirstOrDefault(r => r.Value.GetProperty("id").GetString() == previous); }
        finally { rendering = false; }
        if(currentView=="todos"&&list.SelectedItem is WorkRow selected&&detail.Children.OfType<TextBlock>().FirstOrDefault(t=>Equals(t.Tag,"deadline-counter")) is{} counter)
            counter.Text=Deadline(String(selected.Value,"dueDate"),String(selected.Value,"status"),displayedDay);
        if (rows.Length == 0) feedback.Text = "표시할 항목이 없습니다. 이전 목록은 메뉴를 다시 선택하세요.";
    }
    Task Show(JsonElement value) { if (view == "todos") { Todo(value); return Task.CompletedTask; } return Submission(value.GetProperty("id").GetString()!); }
    static string String(JsonElement value, string field) => value.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    static string Status(string status) => status switch { "suggested" => "추천 · 확인 필요", "open" => "진행 중", "done" => "완료", _ => "제외" };
    static string Deadline(string value,string status,DateOnly today)
    {
        if(!DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var due))return "기한 없음";
        var date=due.ToString("yyyy.MM.dd",CultureInfo.InvariantCulture);
        if(status is "done" or "dismissed")return date;
        var days=due.DayNumber-today.DayNumber;
        return date+" · "+(days>0?"D-"+days:days==0?"D-Day":"기한 지남 "+(-days)+"일");
    }
    void Todo(JsonElement? item)
    {
        detail.Children.Clear(); detail.Children.Add(Text(item is null ? "할 일 직접 추가" : "할 일 확인 · 수정", 19));
        if (item is { } original)
        {
            detail.Children.Add(Text(String(original, "evidence"), 12));
            var counter=Text(Deadline(String(original,"dueDate"),String(original,"status"),KoreanToday),14);counter.Tag="deadline-counter";detail.Children.Add(counter);
            if (String(original, "sourceId") is { Length: > 0 } source)
                detail.Children.Add(Action("원본 메시지 보기", async () => { var originalMessage = await api.Get<Detail>($"api/messages/{source}"); var window = new Window { Owner = this, Title = originalMessage.Message.Title, Width = 550, Height = 450, Style=(Style)FindResource("SchoolWindow"), Content = new ScrollViewer { Content = Text(originalMessage.Message.Body), Margin = new Thickness(20) } }; window.Show(); }));
        }
        var title = new TextBox { MaxLength = 200, Text = item is { } t ? String(t, "title") : "", Margin = new Thickness(0, 0, 0, 12) };
        var due = new DatePicker { Margin = new Thickness(0, 0, 0, 12) };
        if (item is { } d && DateTime.TryParse(String(d, "dueDate"), out var day)) due.SelectedDate = day;
        var statuses = new[] { "open", "done", "dismissed" };
        var status = new ComboBox { ItemsSource = statuses.Select(Status).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 14), IsEnabled = item is not null };
        if (item is { } s && Array.IndexOf(statuses, String(s, "status")) is var index && index >= 0) status.SelectedIndex = index;
        detail.Children.Add(Text("제목", 11)); detail.Children.Add(title); detail.Children.Add(Text("기한 · 선택", 11)); detail.Children.Add(due); detail.Children.Add(Text("상태", 11)); detail.Children.Add(status);
        detail.Children.Add(Action(item is { } candidate && String(candidate, "status") == "suggested" ? "확인 후 등록" : "저장", async () =>
        {
            await api.Send<JsonElement>(item is null ? HttpMethod.Post : HttpMethod.Patch, "api/todos" + (item is { } todo ? "/" + String(todo, "id") : ""), new { title = title.Text, dueDate = due.SelectedDate?.ToString("yyyy-MM-dd"), status = statuses[status.SelectedIndex], revision = item is { } old ? old.GetProperty("revision").GetInt32() : 0 });
            await Reload(); detail.Children.Clear(); detail.Children.Add(Text("저장했습니다."));
        },true));
    }
    public async Task Submission(string id)
    {
        if(view!="submission-requests"){view="submission-requests";offset=0;await Reload();}
        var response = await api.Get<JsonElement>($"api/submission-requests/{id}"); var request = response.GetProperty("request");
        detail.Children.Clear(); detail.Children.Add(Text(String(request, "title"), 19)); detail.Children.Add(Text(String(request, "body")));
        var closed = request.GetProperty("closed").GetBoolean();
        detail.Children.Add(Text("마감 " + DateTimeOffset.FromUnixTimeMilliseconds(request.GetProperty("deadline").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm") + (closed ? " · 종료" : ""), 12));
        var mine = String(request, "ownerId") == api.Session!.User!.Id;
        var targets = response.GetProperty("targets").EnumerateArray().ToArray();
        if(mine){var required=targets.Where(t=>!t.GetProperty("exempt").GetBoolean()).ToArray();var submitted=required.Count(t=>t.GetProperty("submittedAt").ValueKind!=JsonValueKind.Null);var summary=Text($"제출 {submitted}/{required.Length}명 · 미제출 {required.Length-submitted}명 · 면제 {targets.Length-required.Length}명",13);summary.Margin=new Thickness(0);detail.Children.Add(Card(summary,"Pale"));}
        if (mine)
        {
            var actions=new WrapPanel();actions.Children.Add(Action("파일 일괄 다운로드 (ZIP)", async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "제출파일.zip", Filter = "ZIP 파일|*.zip" }; if (dialog.ShowDialog(this) == true) await api.DownloadPath($"api/submission-requests/{id}/zip", dialog.FileName); },true));
            if (!closed) actions.Children.Add(Action("요청 종료", async () => { if (System.Windows.MessageBox.Show(this, "새 제출과 자동 알림을 종료할까요?", "제출 요청 종료", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; await api.Send<JsonElement>(HttpMethod.Post, $"api/submission-requests/{id}/close"); await Submission(id); }));detail.Children.Add(actions);
        }
        var targetGrid=new WrapPanel{ItemWidth=126,HorizontalAlignment=HorizontalAlignment.Stretch,Tag="submission-targets"};targetsPanel=targetGrid;
        targetGrid.SizeChanged+=(_,e)=>{if(e.NewSize.Width<=0)return;var columns=mine?Math.Clamp((int)(e.NewSize.Width/126),1,4):1;targetGrid.ItemWidth=Math.Floor(e.NewSize.Width/columns);};
        detail.Children.Add(targetGrid);
        foreach (var target in targets)
        {
            var exempt = target.GetProperty("exempt").GetBoolean(); var submitted = target.GetProperty("submittedAt").ValueKind != JsonValueKind.Null;
            var name=String(target,"name");var state=exempt?"면제":submitted?target.GetProperty("late").GetBoolean()?"지각 제출":"제출 완료":"미제출";
            var targetDetails=new StackPanel();var header=new Grid{ColumnDefinitions={new(){Width=new GridLength(12)},new(),new(){Width=GridLength.Auto}}};
            var indicator=new System.Windows.Shapes.Ellipse{Width=6,Height=6,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};indicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,exempt?"Muted":submitted?"Blue":"Line");header.Children.Add(indicator);
            var label=Text(name,12);label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");label.FontWeight=FontWeights.SemiBold;label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.Margin=new Thickness(0);label.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(label,1);header.Children.Add(label);targetDetails.Children.Add(header);
            foreach (var file in target.GetProperty("files").EnumerateArray())
            {
                var attachment = file.Deserialize<Attachment>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var filename=Text(attachment.Name,10);filename.TextWrapping=TextWrapping.NoWrap;filename.TextTrimming=TextTrimming.CharacterEllipsis;filename.Margin=new Thickness(0);filename.ToolTip=attachment.Description+(attachment.Expired?" · 파일 만료":"");
                if (attachment.Expired){filename.Margin=new Thickness(0,6,0,0);targetDetails.Children.Add(filename);}
                else
                {
                    var download=Action(attachment.Name, async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = attachment.Name }; if (dialog.ShowDialog(this) == true) await api.Download(attachment.Id, dialog.FileName); });
                    download.Content=filename;download.HorizontalContentAlignment=HorizontalAlignment.Stretch;download.MinWidth=0;download.Padding=new Thickness(4,3,4,3);download.Margin=new Thickness(0,6,0,0);download.ToolTip=attachment.Description;download.Tag="submission-file";
                    System.Windows.Automation.AutomationProperties.SetName(download,attachment.Description);targetDetails.Children.Add(download);
                }
            }
            var card=Card(targetDetails);card.Padding=new Thickness(8);card.CornerRadius=new CornerRadius(9);card.Margin=new Thickness(0,0,8,8);card.MinHeight=48;card.ToolTip=name+" · "+state;card.Tag=String(target,"id");
            System.Windows.Automation.AutomationProperties.SetName(card,name);System.Windows.Automation.AutomationProperties.SetHelpText(card,state);
            if(mine&&!closed)
            {
                var menu=new ContextMenu();var exemption=new MenuItem{Header=exempt?"면제 해제":"제출 면제"};menu.Items.Add(exemption);card.ContextMenu=menu;
                exemption.Click+=async(_,_)=>{if(busy)return;busy=true;try{await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Patch,$"api/submission-requests/{id}/targets/{String(target,"id")}",new{exempt=!exempt});await Submission(id);});}finally{busy=false;}};
                var more=new Button{Content="…",Width=20,Height=22,Padding=new Thickness(0),Margin=new Thickness(2,0,0,0),BorderThickness=new Thickness(0),Background=Brushes.Transparent,ToolTip=name+" 제출 관리"};more.SetResourceReference(Control.ForegroundProperty,"Muted");System.Windows.Automation.AutomationProperties.SetName(more,name+" 제출 관리");
                more.Click+=(_,_)=>{if(busy)return;menu.PlacementTarget=more;menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;menu.IsOpen=true;};Grid.SetColumn(more,2);header.Children.Add(more);
            }
            targetGrid.Children.Add(card);
        }
        if (!mine && !closed && targets.Length > 0 && !targets[0].GetProperty("exempt").GetBoolean())
        {
            detail.Children.Add(Text("답장 없이 파일만 제출할 수 있습니다. 재제출하면 최신 파일로 바뀝니다.",11));
            if(targets[0].GetProperty("submissionId").ValueKind==JsonValueKind.String)
            {
                var submissionId=String(targets[0],"submissionId");
                detail.Children.Add(Action("제출 회수",async()=>
                {
                    if(System.Windows.MessageBox.Show(this,"제출 파일을 회수하고 미제출 상태로 바꿀까요? 이미 다운로드한 사본은 회수할 수 없습니다.","제출 회수",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
                    await api.Send<JsonElement>(HttpMethod.Post,$"api/submission-requests/{id}/withdraw",new{submissionId});pending.Remove(id);await Reload();await Submission(id);
                }));
            }
            detail.Children.Add(Action(pending.ContainsKey(id) ? "같은 제출 다시 시도" : "파일 선택하여 제출 · 재제출", async () =>
            {
                if (!pending.TryGetValue(id, out var packet))
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
                    if (dialog.ShowDialog(this) != true) return;
                    if (dialog.FileNames.Length > 10 || dialog.FileNames.Sum(p => new FileInfo(p).Length) > 209_715_200 || dialog.FileNames.Any(p => new FileInfo(p).Length > 104_857_600)) throw new InvalidOperationException("최대 10개, 파일당 100MB, 합계 200MB입니다.");
                    var uploads = new List<Attachment>(); foreach (var path in dialog.FileNames) uploads.Add(await api.Upload(path));
                    packet = new SubmissionPacket(Guid.NewGuid().ToString(), uploads.Select(a => a.Id).ToArray()); pending[id] = packet;
                }
                await api.Send<JsonElement>(HttpMethod.Post, $"api/submission-requests/{id}/submit", packet); pending.Remove(id); await Reload(); await Submission(id);
            }));
        }
        detail.Children.Add(Text("업로드한 파일은 30일 보관됩니다.",11));
    }
    sealed record WorkRow(JsonElement Value, string Title,string Summary){public string Label=>Title+"\n"+Summary;}
    internal async Task VerifySubmissionLayout(Action<Window,string> capture,string directory)
    {
        var checks=new List<string>();void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks.Add(message);}
        for(var i=0;i<100&&(targetsPanel?.Children.Count??0)<8;i++)await Task.Delay(50);
        Check(targetsPanel?.Children.Count==16,"all sixteen synthetic recipients are rendered");
        void Layout(){UpdateLayout();targetsPanel!.UpdateLayout();UpdateLayout();}
        double Y(int index)=>targetsPanel!.Children[index].TranslatePoint(new Point(),targetsPanel).Y;
        Width=860;Layout();Check(Math.Abs(Y(0)-Y(3))<1&&Y(4)>Y(0),"four recipient cards fit on one row at the normal window size");
        Check(targetsPanel!.Children.Cast<FrameworkElement>().All(c=>c.TranslatePoint(new Point(),targetsPanel).X+c.ActualWidth<=targetsPanel.ActualWidth+1),"recipient cards stay inside the detail viewport without horizontal overflow");
        var file=targetsPanel.Children.Cast<Border>().SelectMany(c=>((StackPanel)c.Child).Children.OfType<Button>()).First(b=>Equals(b.Tag,"submission-file"));var text=(TextBlock)file.Content;
        var full=new FormattedText(text.Text,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface(text.FontFamily,text.FontStyle,text.FontWeight,text.FontStretch),text.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(text).PixelsPerDip);
        Check(text.TextWrapping==TextWrapping.NoWrap&&text.TextTrimming==TextTrimming.CharacterEllipsis&&full.Width>text.ActualWidth&&file.ToolTip!.ToString()!.Contains(text.Text),"an overflowing filename is ellipsized while its full name remains available in the tooltip");
        capture(this,Path.Combine(directory,"submissions-compact.png"));
        Width=650;Layout();Check(Math.Abs(Y(0)-Y(1))<1&&Y(2)>Y(0),"a narrower window reduces the recipient grid to two columns");capture(this,Path.Combine(directory,"submissions-compact-narrow.png"));
        Width=860;Layout();var card=targetsPanel.Children.Cast<Border>().First(c=>System.Windows.Automation.AutomationProperties.GetHelpText(c)=="미제출");var targetId=(string)card.Tag;
        ((MenuItem)card.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));for(var i=0;i<100&&busy;i++)await Task.Delay(50);
        var result=await api.Get<JsonElement>($"api/submission-requests/{requestedId}");Check(result.GetProperty("targets").EnumerateArray().First(t=>String(t,"id")==targetId).GetProperty("exempt").GetBoolean(),"compact recipient management still exempts the selected person through the server");
        card=targetsPanel!.Children.Cast<Border>().First(c=>Equals(c.Tag,targetId));((MenuItem)card.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));for(var i=0;i<100&&busy;i++)await Task.Delay(50);
        result=await api.Get<JsonElement>($"api/submission-requests/{requestedId}");Check(!result.GetProperty("targets").EnumerateArray().First(t=>String(t,"id")==targetId).GetProperty("exempt").GetBoolean(),"exemption can be undone without changing the other recipients");
        var attachment=result.GetProperty("targets").EnumerateArray().SelectMany(t=>t.GetProperty("files").EnumerateArray()).First().Deserialize<Attachment>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var downloaded=Path.Combine(directory,"submission-download-check.txt");await api.Download(attachment.Id,downloaded);Check(File.ReadAllText(downloaded).Contains("synthetic-submission"),"collector can still download the submitted file with its existing authorization");
        Layout();capture(this,Path.Combine(directory,"submissions-compact.png"));File.WriteAllText(Path.Combine(directory,"submission-layout-checks.json"),JsonSerializer.Serialize(new{checks,realSchoolData=false}));
    }
    internal async Task Verify()
    {
        var day=new DateOnly(2026,12,31);
        if(Deadline("2027-01-01","open",day)!="2027.01.01 · D-1"||Deadline("2026-12-31","open",day)!="2026.12.31 · D-Day"
           ||Deadline("2026-12-29","open",day)!="2026.12.29 · 기한 지남 2일"||Deadline("","open",day)!="기한 없음"
           ||Deadline("2026-12-29","done",day)!="2026.12.29")throw new InvalidOperationException("기한별 D-Day 표시 검증 실패");
        view="submission-requests";await Reload();directAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for(var i=0;i<100&&busy;i++)await Task.Delay(50);
        if(busy||view!="todos"||!detail.Children.OfType<TextBox>().Any())throw new InvalidOperationException("제출 탭에서 할 일 직접 추가 전환 실패");
        detail.Children.OfType<TextBox>().Single().Text = "Windows 업무 창에서 등록한 할 일";
        detail.Children.OfType<Button>().Single(b => b.Content?.ToString() == "저장").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && busy; i++) await Task.Delay(50);
        if (busy || !(await api.Get<JsonElement[]>("api/todos")).Any(t => String(t, "title") == "Windows 업무 창에서 등록한 할 일")) throw new InvalidOperationException("Windows 할 일 창 등록 실패: " + feedback.Text);
        await Reload();
        var saved = (await api.Get<JsonElement[]>("api/todos")).First(t => String(t, "title") == "Windows 업무 창에서 등록한 할 일");
        Todo(saved);
    }
    sealed record SubmissionPacket(string ClientId, string[] AttachmentIds);
}
