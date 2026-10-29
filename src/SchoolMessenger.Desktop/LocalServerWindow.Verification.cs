using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    internal async Task Verify(string outputDirectory,bool ownerExit)
    {
        var checks=new List<string>();Directory.CreateDirectory(outputDirectory);
        void Check(bool condition,string text){if(!condition)throw new InvalidOperationException(text);checks.Add(text);}
        async Task Ready(int port)
        {
            using var http=new HttpClient{Timeout=TimeSpan.FromMilliseconds(800)};
            for(var i=0;i<100;i++){try{if((await http.GetAsync("http://127.0.0.1:"+port+"/health")).IsSuccessStatusCode)return;}catch(HttpRequestException){}catch(TaskCanceledException){}await Task.Delay(150);}
            throw new InvalidOperationException("서버 가동 확인 실패: "+string.Join("\n",cards.Select(c=>c.Host.Output)));
        }
        void Capture(string name,Window? target=null)
        {
            target??=this;target.UpdateLayout();var image=new RenderTargetBitmap((int)target.ActualWidth,(int)target.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(target);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(outputDirectory,name));encoder.Save(stream);
        }
        try
        {
            foreach(var c in cards){c.Auto.IsChecked=false;c.Mode.SelectedIndex=0;c.Data.Text=Path.Combine(directory,c.Service);}
            if(ownerExit)
            {
                await Start(cards[0]);await Ready(5080);File.WriteAllText(Path.Combine(outputDirectory,"owner-exit-ready.json"),"{\"ready\":true}");quitting=true;Application.Current.Shutdown();return;
            }
            Check(cards.All(c=>c.Host.Installed&&c.Start.IsEnabled&&!c.Stop.IsEnabled),"server control is available before backend login and both installed roles are stopped");
            Capture("server-manager-stopped.png");
            cards[0].Password.Password="Unsaved synthetic input";SelectCard(cards[1]);UpdateLayout();
            Check(cards[1].View.IsVisible&&!cards[0].View.IsVisible&&cards[0].Password.Password=="Unsaved synthetic input","switching server roles preserves unsubmitted settings and exposes only the selected role");
            cards[0].Password.Clear();cards[1].Mode.SelectedIndex=1;Refresh();
            Check(cards[1].ProductionFields.IsVisible&&!cards[1].TrialFields.IsVisible,"production configuration and trial credentials are never shown together");cards[1].Mode.SelectedIndex=0;
            Width=760;Height=560;UpdateLayout();
            Check(new[]{cards[1].Start,cards[1].Stop,cards[1].Admin}.All(b=>b.IsVisible&&b.TransformToAncestor(this).Transform(new Point()).Y+b.ActualHeight<ActualHeight),"all primary server controls remain visible at the minimum window size");
            Capture("server-manager-compact.png");Width=1060;Height=780;SelectCard(cards[0]);UpdateLayout();
            await Start(cards[0]);Check(!cards[0].Host.Running&&cards[0].Host.Output.Contains("12~128"),"missing first password is shown inside the window without launching a server");
            var password="Synthetic-"+Guid.NewGuid().ToString("N");
            foreach(var c in cards)
            {
                var inherited=new Dictionary<string,string?>();var syntheticSettings=new Dictionary<string,string>{{"FeedbackMail__Enabled","true"},{"FeedbackMail__Host","smtp.synthetic.invalid"},{"FeedbackMail__Sender","sender@example.invalid"},{"FeedbackMail__Recipient","receiver@example.invalid"},{"FeedbackMail__Username","synthetic"},{"FeedbackMail__Password","SYNTHETIC-NOT-A-CREDENTIAL"},{"Registration__PolicyFile",Path.Combine(directory,"missing-other-school-policy.json")}};
                foreach(var pair in syntheticSettings){inherited[pair.Key]=Environment.GetEnvironmentVariable(pair.Key);Environment.SetEnvironmentVariable(pair.Key,pair.Value);}
                try{c.Password.Password=password;c.Confirm.Password=password;await Start(c);}finally{foreach(var pair in inherited)Environment.SetEnvironmentVariable(pair.Key,pair.Value);}
                await Ready(c.Service=="office"?5080:5081);
                Check(c.Host.Running&&c.Password.Password.Length==0&&c.Confirm.Password.Length==0,c.Service+" starts without a console and clears password controls");
                Check(!c.Data.IsEnabled&&!c.Mode.IsEnabled&&!c.ProductionFields.IsEnabled,c.Service+" locks data paths and execution configuration while its server is running");
                using var api=new Api(c.Service=="office"?"http://127.0.0.1:5080":"http://127.0.0.1:5081");await api.Login("admin",password);
                var mailState=await api.Get<JsonElement>("api/admin/feedback/mail-status");Check(!mailState.GetProperty("enabled").GetBoolean()&&!mailState.GetProperty("configured").GetBoolean(),c.Service+" trial does not inherit production mail credentials or another school's registration policy");
                Check((await api.Get<JsonElement>("api/session")).GetProperty("user").ValueKind==JsonValueKind.Object,c.Service+" initializes an administrator and accepts the GUI-supplied password");
                if(c.Service=="office")
                {
                    var operations=new ServerManagementWindow(this,api);operations.Show();
                    try
                    {
                        for(var i=0;i<60&&!operations.Ready;i++)await Task.Delay(100);
                        Check(operations.Ready&&((DockPanel)operations.Content).Children.OfType<TabControl>().Single().Items.Count==3,"authenticated operations loads real resource measurements and separates status, school and connection settings");
                        Capture("server-operations.png",operations);
                    }finally{operations.Close();}
                }
                c.Auto.IsChecked=true;
            }
            await VerifyAdministration(password,Check,Capture);
            SaveSettings();var saved=File.ReadAllText(Path.Combine(directory,"manager.json"));
            Check(!saved.Contains(password)&&File.Exists(Path.Combine(directory,"trial-bridge.dpapi")),"settings retain paths and choices without storing the administrator password; trial bridge key is DPAPI protected");
            LoadSettings();Check(cards.All(c=>c.Auto.IsChecked==true&&c.Data.Text==Path.Combine(directory,c.Service)),"both server roles restore their settings without overwriting the other role");
            startup.SetEnabled(true);Check(startup.Enabled&&StartupCommand.EndsWith(" --server-manager --startup"),"server manager startup uses a separate current-user entry and returns to the dashboard");startup.SetEnabled(false);Check(!startup.Enabled,"server manager startup can be disabled without changing messenger startup");
            Refresh();Capture("server-manager-running.png");
            foreach(var c in cards){await c.Host.Stop();Check(!c.Host.Running,c.Service+" stops gracefully through a local event without process killing");}
            foreach(var c in cards)await BackupBeforeUpdate(c);
            Check(cards.All(c=>Directory.GetFiles(Path.Combine(c.Data.Text,"backups"),"*.db").Length>0),"update preparation creates checked offline database backups for both server roles in their managed retention folders outside the installation directory");
            File.WriteAllText(Path.Combine(directory,"update-resume.json"),JsonSerializer.Serialize(cards.Select(c=>c.Service)));
            foreach(var c in cards)c.Auto.IsChecked=false;
            await ResumeAfterUpdate();await Ready(5080);await Ready(5081);
            Check(cards.All(c=>c.Host.Running)&&!File.Exists(Path.Combine(directory,"update-resume.json")),"post-update restart restores the previously running roles even when normal automatic startup is off");
            foreach(var c in cards){await c.Host.Stop();c.Auto.IsChecked=true;}
            await StartSelected();await Ready(5080);await Ready(5081);using(var api=new Api("http://127.0.0.1:5080")){await api.Login("admin",password);Check(true,"automatic start reuses both selected roles and the existing administrator without requesting or resetting a password");}
            foreach(var c in cards)await c.Host.Stop();
            File.WriteAllText(Path.Combine(outputDirectory,"server-manager-checks.json"),JsonSerializer.Serialize(new{checks,realSchoolData=false}));
        }
        catch(Exception error)
        {
            File.WriteAllText(Path.Combine(outputDirectory,"server-manager-error.txt"),error.ToString()+"\n"+string.Join("\n",cards.Select(c=>c.Host.Output)));Capture("server-manager-error.png");Environment.ExitCode=1;
        }
        finally
        {
            if(!ownerExit){await SignOutAdministrators();foreach(var c in cards){try{await c.Host.Stop();}catch{}}startup.SetEnabled(false);quitting=true;Application.Current.Shutdown(Environment.ExitCode);}
        }
    }
}
