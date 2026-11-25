using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    // Typing a name or department next to the chips lists the matching people; Enter or a click adds one, and Backspace in
    // the empty box takes the last one off. The list follows the typing without rewriting the box, so Korean stays whole.
    readonly TextBox recipientInput=new(){MinWidth=120,BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(4,4,4,4),VerticalAlignment=VerticalAlignment.Center};
    readonly ListBox recipientList=new(){Focusable=false,MaxHeight=240,BorderThickness=new Thickness(0),Background=Brushes.Transparent,DisplayMemberPath="Label"};
    readonly Popup recipientPopup=new(){Placement=PlacementMode.Bottom,AllowsTransparency=true,StaysOpen=true,Focusable=false};
    void InitializeRecipientInput()
    {
        AutomationProperties.SetName(recipientInput,"받는 사람 이름 입력");recipientInput.ToolTip="이름이나 부서를 입력하고 Enter";
        recipientList.ItemContainerStyle=new Style(typeof(ListBoxItem),Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style){Setters={new Setter(Control.PaddingProperty,new Thickness(8,5,8,5)),new Setter(FocusableProperty,false)}};
        var frame=new Border{Child=recipientList,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(4),Margin=new Thickness(0,2,0,0),MinWidth=240};
        frame.SetResourceReference(Border.BackgroundProperty,"Surface");frame.SetResourceReference(Border.BorderBrushProperty,"Line");
        recipientPopup.Child=frame;recipientPopup.PlacementTarget=recipientInput;
        recipientInput.TextChanged+=(_,_)=>SuggestRecipients();
        recipientInput.LostKeyboardFocus+=(_,_)=>recipientPopup.IsOpen=false;
        LocationChanged+=(_,_)=>recipientPopup.IsOpen=false;
        recipientList.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            for(var d=e.OriginalSource as DependencyObject;d is not null&&d!=recipientList;d=d is Visual?VisualTreeHelper.GetParent(d):LogicalTreeHelper.GetParent(d))
                if(d is ListBoxItem{Content:Person person}){AddRecipient(person);e.Handled=true;return;}
        };
        recipientInput.PreviewKeyDown+=(_,e)=>
        {
            var open=recipientPopup.IsOpen&&recipientList.Items.Count>0;
            if(e.Key is Key.Down or Key.Up&&open){recipientList.SelectedIndex=Math.Clamp(recipientList.SelectedIndex+(e.Key==Key.Down?1:-1),0,recipientList.Items.Count-1);recipientList.ScrollIntoView(recipientList.SelectedItem);e.Handled=true;}
            else if(e.Key==Key.Escape&&recipientPopup.IsOpen){recipientPopup.IsOpen=false;e.Handled=true;}
            else if(e.Key is Key.Enter or Key.Tab&&open&&recipientList.SelectedItem is Person person){AddRecipient(person);e.Handled=true;}
            else if(e.Key==Key.Enter){e.Handled=true;}
            else if(e.Key==Key.Back&&recipientInput.Text.Length==0&&pending is null&&!sending&&people.LastOrDefault(p=>p.Selected) is {} last){last.Selected=false;RefreshRecipients();e.Handled=true;}
        };
        // A pasted list ("가상교사01, 가상교사02") adds everyone whose name matches exactly one person.
        DataObject.AddPastingHandler(recipientInput,(_,e)=>
        {
            if(e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted)return;
            var parts=pasted.Split([',',';','\n','\r','\t'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(parts.Length<2)return;
            e.CancelCommand();var left=new List<string>();
            foreach(var part in parts){var hits=people.Where(p=>p.Name==part).ToArray();if(hits.Length==1)hits[0].Selected=true;else left.Add(part);}
            RefreshRecipients();recipientInput.Text=string.Join(", ",left);recipientInput.CaretIndex=recipientInput.Text.Length;
            if(left.Count>0)Feedback.Text="찾지 못했거나 같은 이름이 여럿인 사람: "+string.Join(", ",left);
        });
    }
    // Typing part of a name lists that person first; Enter's pick adds the chip and empties the box; Backspace removes it.
    internal void VerifyRecipientInput(Person given)
    {
        // The staff list may have been refreshed since the caller looked: the person is this window's own copy.
        var other=people.First(p=>p.Id==given.Id);
        foreach(var person in people)person.Selected=false;RefreshRecipients();
        recipientInput.Focus();recipientInput.Text=other.Name[..Math.Max(1,other.Name.Length-1)];SuggestRecipients();
        if(!recipientPopup.IsOpen||recipientList.Items.OfType<Person>().FirstOrDefault()?.Id is not {} first||!recipientList.Items.OfType<Person>().Any(p=>p.Id==other.Id))throw new InvalidOperationException("받는 사람 이름 입력 목록 오류");
        AddRecipient(recipientList.Items.OfType<Person>().First(p=>p.Id==other.Id));
        if(!other.Selected||recipientInput.Text.Length!=0||recipientPopup.IsOpen||!RecipientChips.Children.Contains(recipientInput)||RecipientChips.Children.OfType<Border>().Count()!=1)
            throw new InvalidOperationException($"받는 사람 이름으로 추가 실패: 선택 {other.Selected}, 남은 글 ‘{recipientInput.Text}’, 목록 {recipientPopup.IsOpen}, 칩 {RecipientChips.Children.OfType<Border>().Count()}");
        UpdateLayout();var shown=RecipientChips.Children.OfType<Border>().Single();
        if(shown.ActualWidth<=0||!shown.IsVisible)throw new InvalidOperationException($"추가한 받는 사람 칩이 보이지 않음: 폭 {shown.ActualWidth}, 보임 {shown.IsVisible}, 칸 {RecipientChips.ActualWidth}x{RecipientChips.ActualHeight}, 스크롤 {RecipientScroller.VerticalOffset}/{RecipientScroller.ExtentHeight}, 창 {IsVisible}/{WindowState}");
        recipientInput.Text="목록에 없는 이름";SuggestRecipients();if(recipientPopup.IsOpen)throw new InvalidOperationException("없는 이름에 목록 표시");recipientInput.Clear();
    }
    void SuggestRecipients()
    {
        var text=recipientInput.Text.Trim();
        if(text.Length==0){recipientPopup.IsOpen=false;return;}
        var found=people.Where(p=>!p.Selected&&p.Label.Contains(text,StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p.Name.StartsWith(text,StringComparison.OrdinalIgnoreCase)?0:1).ThenBy(p=>p.Name).Take(30).ToArray();
        recipientList.ItemsSource=found;recipientList.SelectedIndex=found.Length>0?0:-1;
        // Opened once and left open while typing, so the input method is not disturbed by the popup coming and going.
        recipientPopup.IsOpen=found.Length>0;
    }
    void AddRecipient(Person person)
    {
        if(pending is not null||sending)return;
        Ime.Commit(recipientInput);person.Selected=true;recipientPopup.IsOpen=false;recipientInput.Clear();RefreshRecipients();
        Dispatcher.InvokeAsync(()=>recipientInput.Focus(),DispatcherPriority.Input);
    }
    void RefreshRecipients()
    {
        foreach(var old in RecipientChips.Children.OfType<UIElement>().Where(c=>c!=recipientInput).ToArray())RecipientChips.Children.Remove(old);
        if(!RecipientChips.Children.Contains(recipientInput))RecipientChips.Children.Add(recipientInput);
        recipientInput.IsEnabled=pending is null&&!sending;var at=0;
        var selected=people.Where(p=>p.Selected).ToArray();SelfBox.IsChecked=selected.Any(p=>p.Id==main.Api?.Session?.User?.Id);RecipientCount.Text=$"받는 사람 · {selected.Length}명";
        if(selected.Length==0){RecipientChips.Children.Insert(at,new TextBlock{Text="이름을 입력하거나 조직도에서 선택하세요.",Margin=new Thickness(6,8,4,8),Foreground=(Brush)FindResource("Muted"),FontSize=11});return;}
        foreach(var person in selected)
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(Avatar.Create(person.Name,20));row.Children.Add(new TextBlock{Text=person.Name,ToolTip=person.Label,FontSize=12,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,4,0)});
            var remove=new Button{Content="×",ToolTip=person.Name+" 수신자 삭제",Padding=new Thickness(0),Height=18,MinHeight=18,Width=18,MinWidth=18,BorderThickness=new Thickness(0),IsEnabled=pending is null&&!sending};remove.Click+=(_,_)=>{person.Selected=false;RefreshRecipients();};row.Children.Add(remove);
            remove.SetResourceReference(ForegroundProperty,"Muted");remove.Background=Brushes.Transparent;
            var chip=new Border{Child=row,CornerRadius=new CornerRadius(14),Padding=new Thickness(3,3,5,3),Margin=new Thickness(2,2,4,2)};chip.SetResourceReference(Border.BackgroundProperty,"Soft");RecipientChips.Children.Insert(at++,chip);
        }
    }
    void CollectChanged(object sender,RoutedEventArgs e){if(SubmissionDate is not null)SubmissionDate.IsEnabled=CollectFiles.IsChecked==true&&pending is null;}
    void SendToSelf(object sender,RoutedEventArgs e)
    {
        // RefreshRecipients sets the box too; only a change the teacher made should redraw (else the chips draw twice).
        var self=people.FirstOrDefault(p=>p.Id==main.Api?.Session?.User?.Id);
        if(pending is not null||sending||self is null||self.Selected==(SelfBox.IsChecked==true))return;
        foreach(var person in people)if(person.Id==self.Id)person.Selected=SelfBox.IsChecked==true;
        RefreshRecipients();
    }
    void ChooseRecipients(object sender,RoutedEventArgs e)
    {
        var window=new Window{Owner=this,Title="수신자 선택",Width=420,Height=540,WindowStartupLocation=WindowStartupLocation.CenterOwner,Resources=Resources,Style=(Style)Application.Current.FindResource("SchoolWindow")};
        var dock=new DockPanel{Margin=new Thickness(16)};window.Content=dock;
        var search=new TextBox{Margin=new Thickness(0,0,0,9)};var searchHost=AdminUi.SearchField(search,"이름 · 부서 검색");DockPanel.SetDock(searchHost,Dock.Top);dock.Children.Add(searchHost);
        var apply=new Button{Content="선택 적용",Margin=new Thickness(0,10,0,0),Style=(Style)FindResource("PrimaryButton")};DockPanel.SetDock(apply,Dock.Bottom);dock.Children.Add(apply);
        var list=new StackPanel();dock.Children.Add(new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});var picked=people.Where(p=>p.Selected).Select(p=>p.Id).ToHashSet();
        void Render(){list.Children.Clear();foreach(var person in people.Where(p=>p.Label.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p.Department).ThenBy(p=>p.Name)){var check=AdminUi.PersonCheck(person,picked.Contains(person.Id));check.Checked+=(_,_)=>picked.Add(person.Id);check.Unchecked+=(_,_)=>picked.Remove(person.Id);list.Children.Add(check);}}
        search.TextChanged+=(_,_)=>Render();apply.Click+=(_,_)=>{foreach(var person in people)person.Selected=picked.Contains(person.Id);RefreshRecipients();window.Close();};Render();window.ShowDialog();
    }
    void ScheduleChanged(object sender,RoutedEventArgs e)
    {if(ScheduleDate is null)return;ScheduleDate.IsEnabled=ScheduleTime.IsEnabled=ScheduleSend.IsChecked==true&&pending is null;SendButton.Content=ScheduleSend.IsChecked==true?"예약 저장":"보내기";}
    long? ScheduledAt()
    {
        if(ScheduleSend.IsChecked!=true)return null;
        if(ScheduleDate.SelectedDate is not{} day||!TimeOnly.TryParseExact(ScheduleTime.Text,"HH:mm",CultureInfo.InvariantCulture,DateTimeStyles.None,out var time))throw new InvalidOperationException("예약 날짜와 HH:mm 형식의 시간을 입력하세요.");
        return new DateTimeOffset(DateTime.SpecifyKind(day.Date.Add(time.ToTimeSpan()),DateTimeKind.Unspecified),TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
    }
    async void ShowScheduled(object sender,RoutedEventArgs e)
    {
        try
        {
            var window=new Window{Owner=this,Title="내 예약 메시지",Width=510,Height=480,WindowStartupLocation=WindowStartupLocation.CenterOwner,Resources=Resources,Style=(Style)Application.Current.FindResource("SchoolWindow")};var rows=new StackPanel{Margin=new Thickness(18)};window.Content=new ScrollViewer{Content=rows};
            async Task Load(){rows.Children.Clear();var entries=await main.Api!.Get<System.Text.Json.JsonElement[]>("api/messages/scheduled");if(entries.Length==0)rows.Children.Add(AdminUi.Empty("등록된 예약 메시지가 없습니다","‘예약 전송’을 켜고 보내면 보내기 전까지 여기에서 확인하고 취소할 수 있습니다."));foreach(var entry in entries){var id=entry.GetProperty("id").GetString()!;var state=entry.GetProperty("state").GetString();rows.Children.Add(new TextBlock{Text=entry.GetProperty("title").GetString()+"\n"+DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("deliverAt").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm")+" · "+(state switch{"pending"=>"예약 대기","delivered"=>"전송 완료","cancelled"=>"취소",_=>"발송 실패"}),Margin=new Thickness(0,10,0,6),TextWrapping=TextWrapping.Wrap});if(entry.GetProperty("error").ValueKind==System.Text.Json.JsonValueKind.String)rows.Children.Add(new TextBlock{Text=entry.GetProperty("error").GetString(),TextWrapping=TextWrapping.Wrap});if(state=="pending"){var cancel=new Button{Content="예약 취소",HorizontalAlignment=HorizontalAlignment.Left};cancel.Click+=async(_,_)=>{try{await main.Api.Send<System.Text.Json.JsonElement>(HttpMethod.Delete,"api/messages/scheduled/"+id);await Load();}catch(Exception error){Notice.Show(window,error.Message);}};rows.Children.Add(cancel);}}}
            await Load();window.ShowDialog();
        }catch(Exception error){Feedback.Text=error.Message;}
    }
}
