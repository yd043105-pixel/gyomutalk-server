using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    internal void CloseUnsaved(){sent=true;Close();}
    internal void VerifyEditor()
    {
        var original=people.Where(p=>p.Selected).Select(p=>p.Id).ToArray();
        var me=main.Api!.Session!.User!.Id;void Self(bool on){SelfBox.IsChecked=on;SelfBox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));}
        Self(false);var others=people.Where(p=>p.Selected).Select(p=>p.Id).ToArray();
        Self(true);if(!people.Where(p=>p.Selected).Select(p=>p.Id).Order().SequenceEqual(others.Append(me).Order()))throw new InvalidOperationException("나에게도 보내기가 기존 수신자를 지웠습니다");
        Self(false);if(!people.Where(p=>p.Selected).Select(p=>p.Id).Order().SequenceEqual(others.Order()))throw new InvalidOperationException("나에게도 보내기 해제 실패");
        foreach(var person in people)person.Selected=person.Id==me;RefreshRecipients();if(SelfBox.IsChecked!=true)throw new InvalidOperationException("나에게도 보내기 표시 실패");
        var chip=(Border)RecipientChips.Children[0];((StackPanel)chip.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(people.Any(p=>p.Selected)||SelfBox.IsChecked!=false)throw new InvalidOperationException("수신자 삭제 검증 실패");
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
