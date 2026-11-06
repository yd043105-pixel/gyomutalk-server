using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class ComposeWindow
{
    internal async Task VerifyFolderRetry(string directory,Action<bool,string> check)
    {
        var api=main.Api!;
        var source=Path.Combine(directory,"합성 첨부-"+Guid.NewGuid().ToString("N"));
        var folder=Path.Combine(source,"교무 자료");var nested=Path.Combine(folder,"하위");
        Directory.CreateDirectory(nested);Directory.CreateDirectory(Path.Combine(folder,"빈 폴더"));
        var first=Path.Combine(source,"먼저.txt");var second=Path.Combine(nested,"문서.txt");
        File.WriteAllText(first,"first synthetic upload");File.WriteAllText(second,"nested synthetic upload");
        await AddPaths([first,folder]);
        check(files.Count==2&&files[1].IsFolder,"the composer accepts a folder alongside a file");
        using(var locked=new FileStream(second,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            try{await UploadAttachments(api);throw new InvalidOperationException("The locked folder should fail");}
            catch(IOException){}
        }
        var id=uploaded[0]?.Id;
        check(id is not null&&uploaded[1] is null,"a failed folder leaves the successful attachment staged");
        await UploadAttachments(api);
        check(uploaded[0]?.Id==id&&uploaded[1]?.Name=="교무 자료.zip","retry reuses the first upload and sends the folder as one ZIP");
        await api.Send<JsonElement>(System.Net.Http.HttpMethod.Post,"api/messages",new
        {
            clientId=Guid.NewGuid().ToString("N"),title="폴더 시험",body="합성 자료",recipientIds=new[]{api.Session!.User!.Id},attachmentIds=uploaded.Select(file=>file!.Id).ToArray()
        });
        var result=Path.Combine(source,"받은 자료.zip");await api.Download(uploaded[1]!.Id,result);
        using var zip=System.IO.Compression.ZipFile.OpenRead(result);
        using var contents=new StreamReader(zip.GetEntry("교무 자료/하위/문서.txt")!.Open());
        check(zip.GetEntry("교무 자료/빈 폴더/") is not null&&await contents.ReadToEndAsync()=="nested synthetic upload","the downloaded ZIP preserves nested files and empty folders");
        sent=true;File.Delete(draftPath);Close();
    }
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
