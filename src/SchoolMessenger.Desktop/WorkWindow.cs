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
    readonly Grid panes = new();
    readonly Border emptyHost = new(), scroller = new();
    readonly Button todosTab,submissionsTab,directAdd,more;
    readonly StackPanel detail = new() { Margin = new Thickness(24,16,24,16) };
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
    { StatusTone.Watch(feedback);
        this.main = main; api = main.Api!; main.RegisterWorkWindow(this); Resources = main.Resources;
        Title = "교무톡 · 할 일과 파일 제출"; Width = 860; Height = 650; MinWidth = 650; MinHeight = 500; FontSize = 13;
        Style = (Style)FindResource("SchoolWindow"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        requestedId = requestId; if (requestId is not null) view = "submission-requests";
        var layout = new DockPanel();layout.SetResourceReference(Panel.BackgroundProperty,"Surface"); Content = layout;
        var heading = new StackPanel { Margin = new Thickness(24, 16, 24, 0) };
        var headRow=new DockPanel();heading.Children.Add(headRow);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 4, 0, 0) };
        todosTab=Action("나의 할 일", async () => { view = "todos"; offset = 0; detail.Children.Clear(); await Reload(); });todosTab.SetResourceReference(StyleProperty,"TabButton");todosTab.ClearValue(FontSizeProperty);todosTab.ClearValue(MarginProperty);navigation.Children.Add(todosTab);
        submissionsTab=Action("파일 제출 · 수합", async () => { view = "submission-requests"; offset = 0; detail.Children.Clear(); await Reload(); });submissionsTab.SetResourceReference(StyleProperty,"TabButton");submissionsTab.ClearValue(FontSizeProperty);submissionsTab.ClearValue(MarginProperty);navigation.Children.Add(submissionsTab);
        directAdd=Action("할 일 추가", async () => { view="todos";offset=0;await Reload();Todo(null);UpdatePanes(); },true);directAdd.Margin=new Thickness(8,0,0,0);DockPanel.SetDock(directAdd,Dock.Right);headRow.Children.Add(directAdd);var refresh=Action("새로고침", Reload);refresh.Margin=new Thickness(0);DockPanel.SetDock(refresh,Dock.Right);headRow.Children.Add(refresh);headRow.Children.Add(new TextBlock{Text="할 일과 파일 제출",FontSize=22,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center});
        more=Action("다음 100개 보기", async () => { offset += 100; detail.Children.Clear(); await Reload(); });more.Visibility=Visibility.Collapsed;more.Margin=new Thickness(12,0,0,0);more.VerticalAlignment=VerticalAlignment.Center;navigation.Children.Add(more);
        heading.Children.Add(navigation); var headBorder=new Border{Child=heading,BorderThickness=new Thickness(0,0,0,1)};headBorder.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(headBorder, Dock.Top); layout.Children.Add(headBorder);
        DockPanel.SetDock(feedback, Dock.Bottom); layout.Children.Add(feedback);
        var grid = panes; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        list.Margin=new Thickness(12);
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);list.HorizontalContentAlignment=HorizontalAlignment.Stretch;
        var row=new FrameworkElementFactory(typeof(DockPanel));row.SetBinding(ToolTipService.ToolTipProperty,new Binding("Label"));
        var doneBox=new FrameworkElementFactory(typeof(CheckBox));doneBox.SetValue(DockPanel.DockProperty,Dock.Left);doneBox.SetValue(MarginProperty,new Thickness(0,0,12,0));doneBox.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);doneBox.SetBinding(VisibilityProperty,new Binding("CanComplete"){Converter=new BooleanToVisibilityConverter()});doneBox.SetBinding(TagProperty,new Binding());doneBox.SetValue(ToolTipProperty,"완료로 표시");doneBox.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,new RoutedEventHandler(CompleteTodo));row.AppendChild(doneBox);
        var badgeText=new FrameworkElementFactory(typeof(TextBlock));badgeText.SetBinding(TextBlock.TextProperty,new Binding("Badge"));badgeText.SetValue(TextBlock.FontSizeProperty,11d);badgeText.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);badgeText.SetBinding(TextBlock.ForegroundProperty,new Binding("BadgeFore"));
        var badge=new FrameworkElementFactory(typeof(Border));badge.SetValue(DockPanel.DockProperty,Dock.Right);badge.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));badge.SetValue(Border.PaddingProperty,new Thickness(8,2,8,2));badge.SetValue(MarginProperty,new Thickness(8,0,0,0));badge.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);badge.SetBinding(Border.BackgroundProperty,new Binding("BadgeBack"));badge.SetBinding(VisibilityProperty,new Binding("HasBadge"){Converter=new BooleanToVisibilityConverter()});badge.AppendChild(badgeText);row.AppendChild(badge);
        var words=new FrameworkElementFactory(typeof(StackPanel));
        var title=new FrameworkElementFactory(typeof(TextBlock));title.SetBinding(TextBlock.TextProperty,new Binding("Title"));title.SetValue(TextBlock.FontSizeProperty,13d);title.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);title.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);title.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);words.AppendChild(title);
        var summary=new FrameworkElementFactory(typeof(TextBlock));summary.SetBinding(TextBlock.TextProperty,new Binding("Summary"));summary.SetValue(TextBlock.FontSizeProperty,11d);summary.SetResourceReference(TextBlock.ForegroundProperty,"Muted");summary.SetValue(FrameworkElement.MarginProperty,new Thickness(0,6,0,0));summary.SetValue(TextBlock.TextWrappingProperty,TextWrapping.NoWrap);summary.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);words.AppendChild(summary);
        row.AppendChild(words);list.ItemTemplate=new DataTemplate{VisualTree=row};
        list.SelectionChanged += async (_, _) => { if (!rendering && list.SelectedItem is WorkRow row) await Run(() => Show(row.Value)); };
        grid.Children.Add(list); grid.Children.Add(emptyHost); scroller.BorderThickness=new Thickness(1,0,0,0); scroller.SetResourceReference(Border.BorderBrushProperty,"Line"); scroller.Child=new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled }; Grid.SetColumn(scroller, 1); grid.Children.Add(scroller); layout.Children.Add(grid);
        Loaded += async (_, _) => await Run(async () => { await Reload(); if (requestedId is not null) await Submission(requestedId); });
        Loaded+=(_,_)=>deadlineClock.Start();Closed+=(_,_)=>deadlineClock.Stop();
        deadlineClock.Tick+=async(_,_)=>{var today=KoreanToday;if(today!=displayedDay&&!busy){if(view=="todos")await Run(Reload);else displayedDay=today;}};
        Closing += (_, e) => { if (busy && !main.ClosingWorkWindows) e.Cancel = true; };
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
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
        if(primary)button.SetResourceReference(StyleProperty,"PrimaryButton");
        button.Click += async (_, _) => { if (busy) return; busy = true; button.IsEnabled = false; try { await Run(action); } finally { busy = false; button.IsEnabled = true; } }; return button;
    }
    async Task Run(Func<Task> action) { feedback.Text = ""; try { await action(); UpdatePanes(); } catch (Exception error) { feedback.Text = error.Message; } }
    // To-dos fill the window with a side panel for editing; submissions need the wide right side for the recipient cards.
    void UpdatePanes()
    {
        var todos=view=="todos";var open=detail.Children.Count>0;
        panes.ColumnDefinitions[0].Width=todos?new GridLength(1,GridUnitType.Star):new GridLength(280);
        panes.ColumnDefinitions[1].Width=!open&&todos?new GridLength(0):todos?new GridLength(320):new GridLength(1,GridUnitType.Star);
        scroller.Visibility=open||!todos?Visibility.Visible:Visibility.Collapsed;
        var none=list.Items.Count==0;list.Visibility=none?Visibility.Collapsed:Visibility.Visible;emptyHost.Visibility=none?Visibility.Visible:Visibility.Collapsed;
        var add=new Button{Content="할 일 추가"};add.SetResourceReference(StyleProperty,"PrimaryButton");add.Click+=(_,_)=>directAdd.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        emptyHost.Child=todos?AdminUi.Empty("진행 중인 할 일이 없습니다","받은 메시지에서 찾은 할 일과 직접 추가한 일이 여기에 모입니다.",open?null:add):AdminUi.Empty("제출 요청이 없습니다","메시지를 보낼 때 ‘파일 제출 받기’를 켜면 여기에 모입니다.");
    }
    async void CompleteTodo(object sender,RoutedEventArgs e)
    {
        if(sender is not CheckBox{Tag:WorkRow row})return;
        await Run(async()=>{await api.Send<JsonElement>(HttpMethod.Patch,"api/todos/"+String(row.Value,"id"),new{title=String(row.Value,"title"),dueDate=String(row.Value,"dueDate") is{Length:>0} d?d:null,status="done",revision=row.Value.GetProperty("revision").GetInt32()});detail.Children.Clear();await Reload();});
    }
    public async Task Reload()
    {
        var currentView = view;
        todosTab.Tag=currentView=="todos"?"selected":null;submissionsTab.Tag=currentView=="submission-requests"?"selected":null;
        var previous = (list.SelectedItem as WorkRow)?.Value.GetProperty("id").GetString();
        var rows = await api.Get<JsonElement[]>($"api/{view}?offset={offset}");
        if (currentView != view) return;
        feedback.Text="";more.Visibility=rows.Length==100?Visibility.Visible:Visibility.Collapsed;
        // Suggestions come from this PC's copies; they sit after the tasks in progress, as they always have.
        if(currentView=="todos"&&offset==0)rows=rows.TakeWhile(r=>String(r,"status")=="open").Concat(main.Archive.AllSuggestions().Select(s=>JsonSerializer.SerializeToElement(new{id=s.Id,title=s.Title,dueDate=s.DueDate,status="suggested",revision=0,sourceId=s.MessageId,evidence=s.Evidence,updatedAt=s.CreatedAt}))).Concat(rows.SkipWhile(r=>String(r,"status")=="open")).ToArray();
        rendering = true;
        try { displayedDay=KoreanToday;list.ItemsSource = rows.Select(v => (currentView == "todos" ? Status(v.GetProperty("status").GetString()!) + (String(v,"dueDate") is {Length:>0} due ? " · " + due : "") : v.GetProperty("ownerName").GetString() + " · " + v.GetProperty("submittedCount").GetInt32() + "/" + v.GetProperty("targetCount").GetInt32() + "명 제출") is var summary && currentView == "todos" ? WithBadge(v, summary) : new WorkRow(v, v.GetProperty("title").GetString()!.ReplaceLineEndings(" "), summary)).ToArray(); list.SelectedItem = list.Items.Cast<WorkRow>().FirstOrDefault(r => r.Value.GetProperty("id").GetString() == previous); }
        finally { rendering = false; }
        if(currentView=="todos"&&list.SelectedItem is WorkRow selected&&detail.Children.OfType<TextBlock>().FirstOrDefault(t=>Equals(t.Tag,"deadline-counter")) is{} counter)
            counter.Text=Deadline(String(selected.Value,"dueDate"),String(selected.Value,"status"),displayedDay);
        UpdatePanes();
    }
    WorkRow WithBadge(JsonElement v,string summary)
    {
        var state=String(v,"status");var open=state is "open" or "suggested";
        var known=DateOnly.TryParseExact(String(v,"dueDate"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var due);var days=known?due.DayNumber-displayedDay.DayNumber:0;
        var (text,back,fore)=!open?("","",""):!known?("기한 없음","Subtle","Muted"):days<0?("지남 "+(-days)+"일","DangerTint","Danger"):days<=1?(days==0?"D-Day":"D-1","DangerTint","Danger"):days<=3?("D-"+days,"WarningTint","Warning"):("D-"+days,"Subtle","Muted");
        return new WorkRow(v,String(v,"title").ReplaceLineEndings(" "),summary,text,text.Length>0?(Brush)FindResource(back):null,text.Length>0?(Brush)FindResource(fore):null,state=="open");
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
        detail.Children.Clear(); detail.Children.Add(Text(item is null ? "할 일 추가" : "할 일 수정", 16));
        if (item is { } original)
        {
            if (String(original, "evidence") is { Length: > 0 } evidence) detail.Children.Add(Text(evidence, 12));
            var counter=Text(Deadline(String(original,"dueDate"),String(original,"status"),KoreanToday),14);counter.Tag="deadline-counter";detail.Children.Add(counter);
            if (String(original, "sourceId") is { Length: > 0 } source)
                detail.Children.Add(Action("원본 메시지 보기", async () => { var originalMessage = main.Archive.Merge(await api.Get<Detail>($"api/messages/{source}")); if (originalMessage.Message.Purged) { MessageBox.Show(this, "메시지는 받은 PC에만 저장됩니다. 이 PC에는 사본이 없어 내용을 볼 수 없습니다.", "원본 메시지"); return; } var window = new Window { Owner = this, Title = originalMessage.Message.Title, Width = 550, Height = 450, Style=(Style)FindResource("SchoolWindow"), Content = new ScrollViewer { Content = Text(originalMessage.Message.Body), Margin = new Thickness(20) } }; window.Show(); }));
        }
        var title = new TextBox { MaxLength = 200, Text = item is { } t ? String(t, "title") : "", Margin = new Thickness(0, 0, 0, 12) };
        var due = new DatePicker { Margin = new Thickness(0, 0, 0, 12) };
        if (item is { } d && DateTime.TryParse(String(d, "dueDate"), out var day)) due.SelectedDate = day;
        var statuses = new[] { "open", "done", "dismissed" };
        var status = new ComboBox { ItemsSource = statuses.Select(Status).ToArray(), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 14) };
        if (item is { } s && Array.IndexOf(statuses, String(s, "status")) is var index && index >= 0) status.SelectedIndex = index;
        detail.Children.Add(Text("제목", 12)); detail.Children.Add(title); detail.Children.Add(Text("기한 (선택)", 12)); detail.Children.Add(due); if (item is not null) { detail.Children.Add(Text("상태", 12)); detail.Children.Add(status); }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; var cancel = Action("취소", () => { detail.Children.Clear(); list.SelectedItem = null; return Task.CompletedTask; }); buttons.Children.Add(cancel); detail.Children.Add(buttons);
        buttons.Children.Add(Action(item is { } candidate && String(candidate, "status") == "suggested" ? "확인 후 등록" : "저장", async () =>
        {
            if (item is { } suggested && String(suggested, "status") == "suggested")
            {
                if (statuses[status.SelectedIndex] != "dismissed") await api.Send<JsonElement>(HttpMethod.Post, "api/todos", new { title = title.Text, dueDate = due.SelectedDate?.ToString("yyyy-MM-dd"), status = "open", revision = 0, sourceId = String(suggested, "sourceId") });
                main.Archive.Decide(String(suggested, "sourceId"), String(suggested, "id"));
                detail.Children.Clear(); await Reload(); feedback.Text = "저장했습니다."; return;
            }
            await api.Send<JsonElement>(item is null ? HttpMethod.Post : HttpMethod.Patch, "api/todos" + (item is { } todo ? "/" + String(todo, "id") : ""), new { title = title.Text, dueDate = due.SelectedDate?.ToString("yyyy-MM-dd"), status = statuses[status.SelectedIndex], revision = item is { } old ? old.GetProperty("revision").GetInt32() : 0 });
            detail.Children.Clear(); await Reload(); feedback.Text = "저장했습니다.";
        },true));
    }
    public async Task Submission(string id)
    {
        if(view!="submission-requests"){view="submission-requests";offset=0;await Reload();}
        var response = await api.Get<JsonElement>($"api/submission-requests/{id}"); var request = response.GetProperty("request");
        detail.Children.Clear(); detail.Children.Add(Text(String(request, "title"), 16)); detail.Children.Add(Text(String(request, "body")));
        var closed = request.GetProperty("closed").GetBoolean();
        detail.Children.Add(Text("마감 " + DateTimeOffset.FromUnixTimeMilliseconds(request.GetProperty("deadline").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm") + (closed ? " · 종료" : ""), 12));
        var mine = String(request, "ownerId") == api.Session!.User!.Id;
        var targets = response.GetProperty("targets").EnumerateArray().ToArray();
        if(mine){var required=targets.Where(t=>!t.GetProperty("exempt").GetBoolean()).ToArray();var submitted=required.Count(t=>t.GetProperty("submittedAt").ValueKind!=JsonValueKind.Null);var summary=Text($"제출 {submitted}/{required.Length}명 · 미제출 {required.Length-submitted}명 · 면제 {targets.Length-required.Length}명",13);summary.Margin=new Thickness(0);detail.Children.Add(Card(summary,"Pale"));}
        if (mine)
        {
            var actions=new WrapPanel();actions.Children.Add(Action("파일 일괄 다운로드 (ZIP)", async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "제출파일.zip", Filter = "ZIP 파일|*.zip" }; if (dialog.ShowDialog(this) == true) await api.DownloadPath($"api/submission-requests/{id}/zip", dialog.FileName); },true));
            var waiting=targets.Where(t=>!t.GetProperty("exempt").GetBoolean()&&t.GetProperty("submittedAt").ValueKind==JsonValueKind.Null).Select(t=>String(t,"id")).ToArray();
            if (!closed && waiting.Length>0) actions.Children.Add(Action($"미제출자 {waiting.Length}명에게 알림", async () => { var title=String(request,"title");var due=DateTimeOffset.FromUnixTimeMilliseconds(request.GetProperty("deadline").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("M월 d일 HH:mm"); await main.WriteMessage(title: "제출 안내: "+title, recipients: waiting, body: $"‘{title}’ 제출 마감은 {due}입니다. 아직 제출하지 않으셨다면 교무톡의 ‘할 일과 파일 제출’에서 제출해 주세요."); }));
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
            if(String(target,"department") is {Length:>0} department){var unit=Text(department,10);unit.Margin=new Thickness(12,2,0,0);unit.TextTrimming=TextTrimming.CharacterEllipsis;unit.TextWrapping=TextWrapping.NoWrap;targetDetails.Children.Add(unit);}
            foreach (var file in target.GetProperty("files").EnumerateArray())
            {
                var attachment = file.Deserialize<Attachment>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var filename=Text(attachment.Name,10);filename.TextWrapping=TextWrapping.NoWrap;filename.TextTrimming=TextTrimming.CharacterEllipsis;filename.Margin=new Thickness(0);filename.ToolTip=attachment.Description+(attachment.Expired?" · 파일 만료":"");
                if (attachment.Expired){filename.Margin=new Thickness(0,6,0,0);targetDetails.Children.Add(filename);}
                else
                {
                    var download=Action(attachment.Name, async () => { var dialog = new Microsoft.Win32.SaveFileDialog { FileName = attachment.Name }; if (dialog.ShowDialog(this) == true) await AttachmentTransferWindow.Save(this,api,attachment.Id,dialog.FileName); });
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
    sealed record WorkRow(JsonElement Value, string Title,string Summary,string Badge="",Brush? BadgeBack=null,Brush? BadgeFore=null,bool CanComplete=false){public string Label=>Title+"\n"+Summary;public bool HasBadge=>Badge.Length>0;}
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
        if(attachment.Size>128*1024)
        {
            var resumedPath=Path.Combine(directory,"resumed-download-check.txt");
            using var pause=new System.Threading.CancellationTokenSource();
            try{await api.Download(attachment.Id,resumedPath,p=>{if(p.Received>0)pause.Cancel();},pause.Token);}
            catch(OperationCanceledException)when(pause.IsCancellationRequested){}
            Check(pause.IsCancellationRequested&&!File.Exists(resumedPath),"pausing leaves the existing destination untouched");
            long? resumedAt=null;
            await api.Download(attachment.Id,resumedPath,p=>resumedAt??=p.Received);
            Check(resumedAt>0&&resumedAt<attachment.Size&&File.ReadAllBytes(resumedPath).SequenceEqual(File.ReadAllBytes(downloaded)),"native client resumes at the saved offset and verifies the complete file");
        }
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
        detail.Children.OfType<TextBox>().Single().Text = "학급 상담 기록 정리";
        detail.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<Button>()).Single(b => b.Content?.ToString() == "저장").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; i < 100 && busy; i++) await Task.Delay(50);
        if (busy || !(await api.Get<JsonElement[]>("api/todos")).Any(t => String(t, "title") == "학급 상담 기록 정리")) throw new InvalidOperationException("Windows 할 일 창 등록 실패: " + feedback.Text);
        await Reload();
        var saved = (await api.Get<JsonElement[]>("api/todos")).First(t => String(t, "title") == "학급 상담 기록 정리");
        Todo(saved);
    }
    sealed record SubmissionPacket(string ClientId, string[] AttachmentIds);
}
