using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    internal void VerifyEditor()
    {
        var original=people.Where(p=>p.Selected).Select(p=>p.Id).ToArray();
        SelfButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(people.Count(p=>p.Selected)!=1||!people.Single(p=>p.Selected).Id.Equals(main.Api!.Session!.User!.Id))throw new InvalidOperationException("나에게 전송 수신자 검증 실패");
        var chip=(Border)RecipientChips.Children[0];((StackPanel)chip.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(people.Any(p=>p.Selected))throw new InvalidOperationException("수신자 삭제 검증 실패");
        foreach(var person in people)person.Selected=original.Contains(person.Id);RefreshRecipients();
        ScheduleSend.IsChecked=true;ScheduleTime.Text="14:";
        DraftButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(!File.Exists(draftPath)||File.ReadAllBytes(draftPath).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(BodyBox.Text))>=0)throw new InvalidOperationException("임시저장 암호화 검증 실패");
        var restored=new ComposeWindow(main,people,null,null);restored.Show();
        restored.LoadDraft(draftPath);
        if(restored.TitleBox.Text!=TitleBox.Text||restored.BodyBox.Text!=BodyBox.Text||restored.ScheduleSend.IsChecked!=true||restored.ScheduleTime.Text!="14:")throw new InvalidOperationException("미완성 예약을 포함한 임시저장 복원 실패");
        restored.sent=true;restored.Close();ScheduleTime.Text="09:00";ScheduleSend.IsChecked=false;
    }
}
