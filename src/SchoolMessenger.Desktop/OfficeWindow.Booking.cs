using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed partial class OfficeWindow
{
    Task BookingEditor(JsonElement? existing)
    {
        Page();Button("예약 목록으로",()=>Load(true));body.Children.Add(Text("특별실 예약 · 1~7교시",22));
        var title=Field("예약 제목",existing?.GetProperty("title").GetString()??"");title.MaxLength=200;
        var start=existing is{} value?DateTimeOffset.FromUnixTimeMilliseconds(value.GetProperty("start").GetInt64()).ToOffset(SchoolDay.KoreaOffset):DateTimeOffset.UtcNow.ToOffset(SchoolDay.KoreaOffset).AddDays(1);
        var end=existing is{} last?DateTimeOffset.FromUnixTimeMilliseconds(last.GetProperty("end").GetInt64()).ToOffset(SchoolDay.KoreaOffset):start;
        var date=new DatePicker{SelectedDate=start.Date};body.Children.Add(Text("예약 날짜 · 한국 시간"));body.Children.Add(date);
        var grade=new ComboBox{ItemsSource=new[]{"1학년","2학년","3학년"},SelectedIndex=existing is{} old&&old.TryGetProperty("grade",out var g)?Math.Clamp(g.GetInt32()-1,0,2):0};body.Children.Add(Text("학년 · 3학년은 점심시간과 4교시가 다릅니다."));body.Children.Add(grade);
        var first=new ComboBox{DisplayMemberPath="Label"};var lastPeriod=new ComboBox{DisplayMemberPath="Label"};
        body.Children.Add(Text("시작 교시"));body.Children.Add(first);body.Children.Add(Text("마지막 교시"));body.Children.Add(lastPeriod);
        void Choices()
        {
            var periods=SchoolDay.Lessons(grade.SelectedIndex+1,7);var oldFirst=(first.SelectedItem as SchoolPeriod)?.Number;var oldLast=(lastPeriod.SelectedItem as SchoolPeriod)?.Number;
            first.ItemsSource=lastPeriod.ItemsSource=periods;
            first.SelectedItem=periods.FirstOrDefault(p=>p.Number==oldFirst)??periods.FirstOrDefault(p=>p.Start==start.ToString("HH:mm"))??periods[0];
            lastPeriod.SelectedItem=periods.FirstOrDefault(p=>p.Number==oldLast)??periods.FirstOrDefault(p=>p.End==end.ToString("HH:mm"))??first.SelectedItem;
        }
        grade.SelectionChanged+=(_,_)=>Choices();Choices();
        var rooms=setup.GetProperty("rooms").EnumerateArray().Where(r=>r.GetProperty("active").GetBoolean()).Select(r=>new Choice(r.GetProperty("id").GetString()!,r.GetProperty("name").GetString()!)).ToArray();
        var room=new ComboBox{ItemsSource=rooms,SelectedIndex=rooms.Length>0?0:-1};if(existing is{} previous)room.SelectedItem=rooms.FirstOrDefault(r=>r.Id==previous.GetProperty("roomId").GetString());body.Children.Add(Text("특별실"));body.Children.Add(room);
        body.Children.Add(Text("최종 예약 종료는 7교시 16:40입니다. 조회·점심·청소·8교시·야간은 별도 예약할 수 없습니다. 여러 교시를 선택하면 중간 쉬는 시간도 예약 구간에 포함됩니다.",12));
        var client=existing?.GetProperty("clientId").GetString()??Guid.NewGuid().ToString("N");
        Button("특별실 예약 저장",async()=>
        {
            if(date.SelectedDate is not{} selected||room.SelectedItem is not Choice chosen||first.SelectedItem is not SchoolPeriod a||lastPeriod.SelectedItem is not SchoolPeriod b||a.Number>b.Number)throw new InvalidOperationException("날짜·특별실·교시 순서를 확인하세요.");
            var packet=new OfficeBookingRequest(client,chosen.Id,title.Text,SchoolDay.Timestamp(selected,a.Start),SchoolDay.Timestamp(selected,b.End),existing?.GetProperty("revision").GetInt32()??0,grade.SelectedIndex+1);
            await api.Send<JsonElement>(HttpMethod.Post,"api/office/bookings"+(existing is{} old?"/"+old.GetProperty("id").GetString():""),packet);await Load(true);
        });return Task.CompletedTask;
    }
}
