using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using Orientation=System.Windows.Controls.Orientation;

namespace SchoolMessenger.Desktop;

public sealed class CounselingWindow : Window
{
    readonly MainWindow main;
    readonly Api api;
    readonly StackPanel body=new(){Margin=new Thickness(22)};
    readonly StackPanel list=new();
    readonly StackPanel form=new();
    readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12)};
    readonly DatePicker day=new(){SelectedDate=DateOnly.Parse(CounselingTimes.Today).ToDateTime(TimeOnly.MinValue).AddDays(1)};
    readonly DatePicker opensDay=new(){SelectedDate=DateOnly.Parse(CounselingTimes.Today).ToDateTime(TimeOnly.MinValue)};
    readonly ComboBox room=new(){DisplayMemberPath="Name",Margin=new Thickness(0,0,0,10)};
    readonly ComboBox opensHour=new(){ItemsSource=Enumerable.Range(0,24).Select(h=>$"{h:00}").ToArray(),SelectedIndex=9};
    readonly ComboBox opensMinute=new(){ItemsSource=new[]{"00","10","20","30","40","50"},SelectedIndex=0};
    readonly CheckBox scheduled=new(){Content="예약 신청을 지정 시각에 열기",Margin=new Thickness(0,8,0,8)};
    readonly Dictionary<int,CheckBox> periods=[];
    readonly WrapPanel choices=new();
    CounselingOpen? pending;
    bool busy;
    internal bool Ready {get;private set;}
    public CounselingWindow(MainWindow main)
    {
        this.main=main;api=main.Api!;Owner=main;Resources=main.Resources;Title="교무톡 · 상담 예약";
        Width=680;Height=760;MinWidth=550;MinHeight=520;Style=(Style)FindResource("SchoolWindow");WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Content=new ScrollViewer{Content=body};body.Children.Add(Text("상담 예약",23));body.Children.Add(Text("담당 학급의 상담 시간을 열고 신청 현황을 확인하세요. 모든 시각은 한국 시간입니다."));body.Children.Add(feedback);
        body.Children.Add(Text("상담 날짜",12));body.Children.Add(day);AddAction(body,"선택한 날짜 조회 · 새로고침",async()=>{await Load();pending=null;form.IsEnabled=true;});
        form.Children.Add(Text("예약 시간 열기",17));form.Children.Add(room);
        room.SelectionChanged+=(_,_)=>PeriodChoices();form.Children.Add(choices);
        form.Children.Add(scheduled);var opening=new StackPanel{Orientation=Orientation.Horizontal};opening.Children.Add(opensDay);opening.Children.Add(opensHour);opening.Children.Add(Text(":"));opening.Children.Add(opensMinute);form.Children.Add(opening);body.Children.Add(form);
        AddAction(body,"선택한 상담 시간 열기 · 실패 시 같은 요청 재시도",async()=>
        {
            if(pending is null)
            {
                if(room.SelectedItem is not PortalClass chosen||day.SelectedDate is not{} selected)throw new InvalidOperationException("담당 학급과 날짜를 선택하세요.");
                long? opens=null;if(scheduled.IsChecked==true){if(opensDay.SelectedDate is not{} openingDate)throw new InvalidOperationException("예약 오픈 날짜를 선택하세요.");opens=CounselingTimes.At(DateOnly.FromDateTime(openingDate),$"{opensHour.SelectedItem}:{opensMinute.SelectedItem}");}
                pending=new CounselingOpen(Guid.NewGuid().ToString("N"),chosen.Id,selected.ToString("yyyy-MM-dd"),periods.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray(),opens);
                form.IsEnabled=false;
            }
            await api.Send<object>(HttpMethod.Post,"api/counseling/slots",pending);pending=null;form.IsEnabled=true;await Load();feedback.Text="상담 시간을 열었습니다.";
        });
        body.Children.Add(list);Loaded+=async(_,_)=>await Run(Load);
    }
    static TextBlock Text(string text,int size=13)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,10)};
    void PeriodChoices()
    {
        var selected=periods.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray();periods.Clear();choices.Children.Clear();
        if(room.SelectedItem is not PortalClass {Grade:>=1 and <=3} chosen)return;
        foreach(var period in CounselingTimes.Periods(chosen.Grade)){var box=new CheckBox{Content=period.Label,IsChecked=selected.Contains(period.Id),Margin=new Thickness(0,0,16,12)};periods[period.Id]=box;choices.Children.Add(box);}
    }
    void AddAction(StackPanel panel,string label,Func<Task> action)
    {var button=new Button{Content=label,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,10)};button.Click+=async(_,_)=>await Run(action);panel.Children.Add(button);}
    async Task Run(Func<Task> action)
    {if(busy)return;if(main.Api!=api){Close();return;}busy=true;feedback.Text="";try{await action();}catch(Exception e){feedback.Text=e.Message;}finally{busy=false;}}
    async Task Load()
    {
        Ready=false;
        if(day.SelectedDate is not{} selected)throw new InvalidOperationException("상담 날짜를 선택하세요.");
        var value=await api.Get<CounselingView>("api/counseling?date="+selected.ToString("yyyy-MM-dd"));if(main.Api!=api||!IsLoaded)return;
        var chosen=(room.SelectedItem as PortalClass)?.Id;room.ItemsSource=value.Classes;room.SelectedItem=value.Classes.FirstOrDefault(c=>c.Id==chosen)??value.Classes.FirstOrDefault();
        list.Children.Clear();if(value.Classes.Length==0){list.Children.Add(Text("관리자에게 학교 소식 교사 계정 연결과 담당 학급 배정을 요청하세요."));Ready=true;return;}
        list.Children.Add(Text("선택한 날짜의 상담",18));if(value.Slots.Length==0)list.Children.Add(Text("열린 시간이 없습니다. 학급과 시간대를 선택해 열어주세요."));
        foreach(var slot in value.Slots)
        {
            var panel=new StackPanel{Margin=new Thickness(14)};
            panel.Children.Add(Text(CounselingTimes.Display(slot.StartsAt)+"–"+DateTimeOffset.FromUnixTimeMilliseconds(slot.EndsAt).ToOffset(CounselingTimes.KoreaOffset).ToString("HH:mm")+" · "+slot.ClassName,15));
            var booking=value.Bookings.FirstOrDefault(b=>b.SlotId==slot.Id&&b.CanceledAt is null);
            panel.Children.Add(Text(slot.Closed?"닫힘":slot.Booked?"예약 완료":slot.OpensAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()?"신청 오픈 예정 · "+CounselingTimes.Display(slot.OpensAt):"신청 가능"));
            if(booking is not null)panel.Children.Add(Text(booking.StudentName+" · "+(booking.RequesterRole=="parent"?"학부모 신청":"학생 신청"),14));
            if(slot.StartsAt>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                if(booking is not null)AddAction(panel,"이 예약 취소",async()=>{if(MessageBox.Show(this,booking.StudentName+"님의 상담 예약을 취소할까요?","예약 취소",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;await api.Send<object>(HttpMethod.Post,"api/counseling/bookings/"+booking.Id+"/cancel");await Load();});
                else if(!slot.Closed&&!slot.Booked)AddAction(panel,"이 시간 닫기",async()=>{await api.Send<object>(HttpMethod.Post,"api/counseling/slots/"+slot.Id+"/close");await Load();});
            }
            var card=new Border{CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1),Child=panel,Margin=new Thickness(0,0,0,12)};
            card.SetResourceReference(Border.BackgroundProperty,"Surface");card.SetResourceReference(Border.BorderBrushProperty,"Line");list.Children.Add(card);
        }
        Ready=true;
    }
}
