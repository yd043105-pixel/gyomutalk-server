using System.Diagnostics;
using System.Windows;

namespace SchoolMessenger.Desktop;
public sealed partial class LocalServerWindow
{
    readonly List<ServerCard> updateRestart=new();
    internal async Task<bool> PrepareForUpdate()
    {
        if(cards.Any(c=>c.Busy)||OwnedWindows.Cast<Window>().Any(w=>w.IsVisible))return false;
        if(MessageBox.Show(this,"서버를 정상 중지하고 전체 백업을 완료한 뒤 업데이트합니다. 연결된 사용자의 서비스가 잠시 중단됩니다. 진행할까요?","서버 업데이트",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return false;
        updateRestart.Clear();updateRestart.AddRange(cards.Where(c=>c.Host.Running));
        foreach(var card in cards)card.Busy=true;Refresh();
        try
        {
            await SignOutAdministrators();
            foreach(var card in cards)await card.Host.Stop();
            foreach(var card in cards.Where(c=>c.Host.Installed))
            {
                if(card.Mode.SelectedIndex==0&&!File.Exists(Path.Combine(card.Data.Text,".initialized")))continue;
                await BackupBeforeUpdate(card);
            }
            SaveSettings();
            // Start precisely the services that were running, even if ordinary auto-start is off.
            File.WriteAllText(Path.Combine(directory,"update-resume.json"),System.Text.Json.JsonSerializer.Serialize(updateRestart.Select(c=>c.Service)));
            return true;
        }
        catch(Exception error){await CancelUpdate();MessageBox.Show(this,"백업·정상 중지가 완료되지 않아 업데이트를 중단했습니다.\n"+error.Message,"서버 업데이트 중단");return false;}
    }
    async Task BackupBeforeUpdate(ServerCard card)
    {
        var trial=card.Mode.SelectedIndex==0;
        var start=new ProcessStartInfo(trial?card.Host.Executable:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(card.Host.Executable)!};
        foreach(var key in start.Environment.Keys.Where(k=>new[]{"School__","Portal__","Operations__","Announcements__","Registration__","FeedbackMail__","YY_MANAGED_"}.Any(p=>k.StartsWith(p,StringComparison.OrdinalIgnoreCase))).ToArray())start.Environment.Remove(key);
        if(trial)
        {
            var prefix=card.Service=="office"?"School":"Portal";
            start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";
            start.Environment[prefix+"__DataDirectory"]=Path.GetFullPath(card.Data.Text);
            // Reuse the backend's managed backup location so expiry/graduation erasure also covers update backups.
            start.Environment[prefix+"__BackupDirectory"]=Path.Combine(Path.GetFullPath(card.Data.Text),"backups");
            start.Environment[prefix+"__AttachmentBackup"]="true";
            start.ArgumentList.Add("--operations-backup");
        }
        else
        {
            using var config=System.Text.Json.JsonDocument.Parse(File.ReadAllText(card.Config.Text));
            if(!config.RootElement.TryGetProperty("attachmentBackup",out var attachments)||!attachments.GetBoolean())throw new IOException("업데이트 전 전체 백업을 위해 운영 설정의 attachmentBackup을 true로 설정하세요.");
            foreach(var value in new[]{"-NoProfile","-NonInteractive","-File",Path.Combine(root,"scripts","Server-Operations.ps1"),"-Mode","Backup","-ConfigurationPath",card.Config.Text})start.ArgumentList.Add(value);
        }
        using var process=Process.Start(start)??throw new IOException("백업 프로그램을 시작하지 못했습니다.");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        await stdout;var failure=await stderr;
        if(process.ExitCode!=0)throw new IOException("서버 백업 실패: "+card.Service+". 운영 기록을 확인하세요.");
        card.Host.WriteOutput("업데이트 전 백업 완료");
    }
    internal async Task CancelUpdate()
    {
        foreach(var card in cards)card.Busy=false;
        var resume=Path.Combine(directory,"update-resume.json");if(File.Exists(resume))File.Delete(resume);
        foreach(var card in updateRestart)await Start(card);updateRestart.Clear();Refresh();
    }
    internal void CompleteUpdateShutdown(){quitting=true;Close();Application.Current.Shutdown();}
    async Task ResumeAfterUpdate()
    {
        var resume=Path.Combine(directory,"update-resume.json");
        if(!File.Exists(resume)){await StartSelected();return;}
        try{var services=System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(resume))??[];File.Delete(resume);foreach(var card in cards.Where(c=>services.Contains(c.Service)))await Start(card);}
        catch(Exception e){MessageBox.Show(this,e.Message,"업데이트 후 서버 가동 확인");}
    }
}
