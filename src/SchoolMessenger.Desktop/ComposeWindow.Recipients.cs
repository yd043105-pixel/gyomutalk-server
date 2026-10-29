using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    void RefreshRecipients()
    {
        RecipientChips.Children.Clear();var selected=people.Where(p=>p.Selected).ToArray();RecipientCount.Text=$"받는 사람 · {selected.Length}명";
        if(selected.Length==0){RecipientChips.Children.Add(new TextBlock{Text="수신자를 추가하거나 조직도에서 선택하세요.",Margin=new Thickness(8),Foreground=(Brush)FindResource("Muted"),FontSize=11});return;}
        foreach(var person in selected)
        {
            var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(new TextBlock{Text=person.Label,FontSize=11,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,6,0)});
            var remove=new Button{Content="×",ToolTip=person.Name+" 수신자 삭제",Padding=new Thickness(0),Height=18,MinHeight=18,Width=18,MinWidth=18,BorderThickness=new Thickness(0),IsEnabled=pending is null&&!sending};remove.Click+=(_,_)=>{person.Selected=false;RefreshRecipients();};row.Children.Add(remove);
            RecipientChips.Children.Add(new Border{Child=row,Background=(Brush)FindResource("Surface"),BorderBrush=(Brush)FindResource("Line"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),Padding=new Thickness(5,2,3,2),Margin=new Thickness(3,2,3,2)});
        }
    }
    void SendToSelf(object sender,RoutedEventArgs e)
    {
        if(pending is not null||sending)return;
        foreach(var person in people)person.Selected=person.Id==main.Api!.Session!.User!.Id;
        RefreshRecipients();Feedback.Text="받는 사람을 나로 지정했습니다.";
    }
    void ChooseRecipients(object sender,RoutedEventArgs e)
    {
        var window=new Window{Owner=this,Title="수신자 선택",Width=420,Height=540,WindowStartupLocation=WindowStartupLocation.CenterOwner,Resources=Resources};
        var dock=new DockPanel{Margin=new Thickness(16)};window.Content=dock;
        var search=new TextBox{Margin=new Thickness(0,0,0,9)};DockPanel.SetDock(search,Dock.Top);dock.Children.Add(search);
        var apply=new Button{Content="선택 적용",Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(apply,Dock.Bottom);dock.Children.Add(apply);
        var list=new StackPanel();dock.Children.Add(new ScrollViewer{Content=list});var picked=people.Where(p=>p.Selected).Select(p=>p.Id).ToHashSet();
        void Render(){list.Children.Clear();foreach(var person in people.Where(p=>p.Label.Contains(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p.Department).ThenBy(p=>p.Name)){var check=new CheckBox{Content=person.Label,IsChecked=picked.Contains(person.Id),Margin=new Thickness(0,6,0,6)};check.Checked+=(_,_)=>picked.Add(person.Id);check.Unchecked+=(_,_)=>picked.Remove(person.Id);list.Children.Add(check);}}
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
            var window=new Window{Owner=this,Title="내 예약 메시지",Width=510,Height=480,WindowStartupLocation=WindowStartupLocation.CenterOwner,Resources=Resources};var rows=new StackPanel{Margin=new Thickness(18)};window.Content=new ScrollViewer{Content=rows};
            async Task Load(){rows.Children.Clear();var entries=await main.Api!.Get<System.Text.Json.JsonElement[]>("api/messages/scheduled");if(entries.Length==0)rows.Children.Add(new TextBlock{Text="등록된 예약 메시지가 없습니다."});foreach(var entry in entries){var id=entry.GetProperty("id").GetString()!;var state=entry.GetProperty("state").GetString();rows.Children.Add(new TextBlock{Text=entry.GetProperty("title").GetString()+"\n"+DateTimeOffset.FromUnixTimeMilliseconds(entry.GetProperty("deliverAt").GetInt64()).ToOffset(TimeSpan.FromHours(9)).ToString("yyyy.MM.dd HH:mm")+" · "+(state switch{"pending"=>"예약 대기","delivered"=>"전송 완료","cancelled"=>"취소",_=>"발송 실패"}),Margin=new Thickness(0,10,0,6),TextWrapping=TextWrapping.Wrap});if(entry.GetProperty("error").ValueKind==System.Text.Json.JsonValueKind.String)rows.Children.Add(new TextBlock{Text=entry.GetProperty("error").GetString(),TextWrapping=TextWrapping.Wrap});if(state=="pending"){var cancel=new Button{Content="예약 취소",HorizontalAlignment=HorizontalAlignment.Left};cancel.Click+=async(_,_)=>{try{await main.Api.Send<System.Text.Json.JsonElement>(HttpMethod.Delete,"api/messages/scheduled/"+id);await Load();}catch(Exception error){MessageBox.Show(window,error.Message);}};rows.Children.Add(cancel);}}}
            await Load();window.ShowDialog();
        }catch(Exception error){Feedback.Text=error.Message;}
    }
}
