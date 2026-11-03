using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public sealed partial class LocalServerWindow
{
    // Synthetic school only: invented name, account and approval file; the generated certificate is removed afterwards.
    internal async Task VerifySimple(string outputDirectory)
    {
        var checks=new List<string>();Directory.CreateDirectory(outputDirectory);
        void Check(bool condition,string text){if(!condition)throw new InvalidOperationException(text);checks.Add(text);}
        SimpleOperation.PortOffset=1000;var card=cards.First(c=>c.Service=="office");var address="https://127.0.0.1:"+SimpleOperation.Port("office");var password="Simple-"+Guid.NewGuid().ToString("N");
        SimpleOperation.Bind="127.0.0.1";CertificatePins.UseFolder(Path.Combine(directory,"client"));
        try
        {
            Check(card.Mode.SelectedIndex==2,"a new office server starts in the recommended simple setup");
            var news=cards.First(c=>c.Service=="announcements");Refresh();
            Check(news.Status.Text=="미설치"&&news.Activate.Visibility==Visibility.Visible&&news.Controls.Visibility!=Visibility.Visible,"the student and parent news server is shown as not installed until the school chooses to install it");
            card.SimpleData.Text=Path.Combine(directory,"simple","office");card.Backup.Text=Path.Combine(directory,"simple-backup","office");
            // Two real schools sharing a name: only the address tells them apart.
            var twins=SchoolList.All.Where(s=>s.Address.Length>0).GroupBy(s=>s.Name).First(g=>g.Count()>1).ToArray();var school=twins[1];
            Check(SchoolList.All.Length>12000&&SchoolList.Search(school.Name+" "+school.Address.Split(' ')[0]).Contains(school),"the school list searches by name and region");
            card.School.Text=school.Name;card.AdminUsername.Text="it.manager";
            var approval=Path.Combine(directory,"synthetic-approval.pdf");File.WriteAllText(approval,"synthetic principal approval for verification");
            var (kept,hash)=SimpleOperation.KeepApproval(directory,"office",approval);card.Approval.Text=kept;card.ApprovalSha256=hash;card.ReviewedAt=DateTimeOffset.Now;
            card.Password.Password=card.Confirm.Password=password;SelectCard(card);Refresh();UpdateLayout();
            var ambiguous=false;try{PrepareSimple(card);}catch(InvalidOperationException e){ambiguous=e.Message.Contains("이름이 같은 학교");}
            Check(ambiguous&&card.SchoolPick!.Choice.Text.Contains("같은 이름의 학교"),"a typed school name shared by several schools must be picked from the list by address");
            card.SchoolPick.Set(school.Name,school.Address);Check(card.SchoolPick.Selected==school&&card.SchoolPick.Choice.Text.Contains(school.Address),"the picked school shows its address");
            Check(card.SimpleFields.IsVisible&&card.PasswordFields.IsVisible&&!card.TrialFields.IsVisible&&!card.ProductionFields.IsVisible,"simple setup asks only for school name, administrator account and approval document");
            var noPolicy=false;try{RenderPolicy(new(school.Name,"","교감","2026-11-03","사용하지 않음"));}catch(InvalidOperationException){noPolicy=true;}
            Check(noPolicy&&ConfirmedPolicy(card.SimpleData.Text) is null,"sign-ups wait until the school confirms its terms with a contact");
            ConfirmPolicy(card.SimpleData.Text,new(school.Name,"교무실 031-000-0000","교감 가상","2026-11-03","사용하지 않음"));
            await Start(card);
            Check(card.Host.Running&&card.Fingerprint.Length==64,"the server starts in production mode with a generated certificate");
            Check(CertificatePins.ValidateFingerprint(new Uri(address+"/"),card.Fingerprint),"the administrator program trusts the certificate it generated for its own sign-in");
            CertificatePins.UseFolder(Path.Combine(directory,"teacher-pc")); // from here on, act as a teacher PC that has never connected
            async Task<bool> Ready(){using var http=new HttpClient(CertificatePins.Handler(new Uri(address+"/"),new HttpClientHandler())){Timeout=TimeSpan.FromSeconds(2)};for(var i=0;i<80;i++){try{if((await http.GetAsync(address+"/health")).IsSuccessStatusCode)return true;}catch(HttpRequestException){}catch(TaskCanceledException){}await Task.Delay(250);}return false;}
            Check(!await Ready()&&CertificatePins.Pending(address)==card.Fingerprint,"an unconfirmed school certificate is refused and offered for code comparison");
            CertificatePins.Trust(address,card.Fingerprint);
            Check(await Ready(),"after the connection code is confirmed the same certificate is accepted");
            using(var api=new Api(address))
            {
                await api.Login("it.manager",password);
                Check(api.Session?.User?.IsAdmin==true,"the administrator ID chosen at setup signs in over HTTPS");
                var info=await api.Get<JsonElement>("server-info");
                var signup=await api.Get<JsonElement>("api/registration/policy");var terms=signup.GetProperty("documents")[0].GetProperty("body").GetString()!;
                Check(signup.GetProperty("canRegister").GetBoolean()&&!signup.GetProperty("draft").GetBoolean()&&signup.GetProperty("operator").GetString()==school.Name&&terms.Contains("교무실 031-000-0000")&&!terms.Contains("{{")&&!terms.Contains("초안"),"the confirmed school terms open sign-up as the official service, with the school's name and contact");
                var applied=await api.Send<JsonElement>(HttpMethod.Post,"api/register",new{username="new.teacher",name="가상 교사",department="교무부",password="Teacher-"+Guid.NewGuid().ToString("N")[..12],agreement=new{revision=signup.GetProperty("revision").GetString(),acceptedIds=new[]{"terms","privacy"}}});
                Check(applied.GetProperty("status").GetString()=="pending","a teacher applies for an account under the confirmed terms and waits for approval");
                Check(info.GetProperty("schoolName").GetString()==school.Name&&info.GetProperty("schoolAddress").GetString()==school.Address,"the school name and address identify the server for teacher PCs");
                Check(SchoolList.Mismatch(school,school.Name,school.Address) is null&&SchoolList.Mismatch(twins[0],school.Name,school.Address) is {} wrong&&wrong.Contains(school.Address),"a user who picked the same-named school elsewhere is told the address belongs to another school");
            }
            using(var wrong=new Api(address)){var rejected=false;try{await wrong.Login("admin",password);}catch(InvalidOperationException){rejected=true;}Check(rejected,"the default admin ID is not created when another ID is chosen");}
            var discovery=JsonDocument.Parse(File.ReadAllText(Path.Combine(card.SimpleData.Text,"discovery.json"))).RootElement;
            Check(discovery.GetProperty("enabled").GetBoolean()&&discovery.GetProperty("address").GetString()!.StartsWith("https://",StringComparison.Ordinal),"teacher PC discovery is turned on with the HTTPS address");
            var link=SchoolLink(address,card.Fingerprint,school.Name,school.Address,"office");var linked=MoveFrom(link,0);
            Check(linked.Address==address+"/"&&linked.Fingerprint==card.Fingerprint&&link.Contains(Uri.EscapeDataString(school.Address)),"the school connection link carries the server address, its certificate and the school");
            // The same start the administrator gets from the dialog: review record, loopback entrance and the read-only list together.
            async Task<System.Net.HttpStatusCode> Outside(string path,string host="gyomutalk.school.example")
            {
                using var http=new HttpClient();using var request=new HttpRequestMessage(HttpMethod.Get,SimpleOperation.ExternalListener+path);request.Headers.Host=host;
                using var response=await http.SendAsync(request);return response.StatusCode;
            }
            Check(ExternalHostFrom(" https://Gyomutalk.School.Example/ ").Equals("gyomutalk.school.example",StringComparison.OrdinalIgnoreCase)&&ExternalHostFrom("")==""&&new[]{"http://a.example","https://10.0.0.1","https://a.example:8443","https://a.example/x","localhost"}.All(bad=>{try{ExternalHostFrom(bad);return false;}catch(InvalidOperationException){return true;}}),"only a plain school domain is accepted as the public address");
            await card.Host.Stop();card.ExternalHost="gyomutalk.school.example";await Start(card);
            Check(await Ready()&&await Outside("/api/session")==System.Net.HttpStatusCode.OK&&await Outside("/api/admin/users")==System.Net.HttpStatusCode.Forbidden&&await Outside("/api/session","other.example")==System.Net.HttpStatusCode.NotFound,"with the public address set, the server opens the read-only entrance and passes its own review record");
            Check(!System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(l=>l.Port==SimpleOperation.ExternalPort&&!System.Net.IPAddress.IsLoopback(l.Address)),"the entrance listens on this PC only");
            await card.Host.Stop();card.ExternalHost="";await Start(card);var closedEntrance=false;try{await Outside("/health");}catch(HttpRequestException){closedEntrance=true;}
            Check(await Ready()&&closedEntrance,"turning it off closes the entrance");
            (string[] Texts,bool Qr) MoveDialog(bool ready,string file,Action? show=null)
            {
                AppFeatures.Ready=ready;string[] texts=[];var qr=false;
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    var dialog=OwnedWindows.Cast<Window>().Last();texts=ViewControls<TextBlock>(dialog).Select(t=>t.Text).ToArray();qr=ViewControls<Image>(dialog).Any(i=>i.IsVisible);
                    var picture=new RenderTargetBitmap((int)dialog.ActualWidth,(int)dialog.ActualHeight,96,96,PixelFormats.Pbgra32);picture.Render((Visual)dialog.Content);
                    var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(picture));using(var stream=File.Create(Path.Combine(outputDirectory,file)))png.Save(stream);
                    dialog.Close();
                }),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                (show??(()=>ShowSchoolLink(card)))();AppFeatures.Ready=true;return (texts,qr);
            }
            var closed=MoveDialog(false,"move-dialog-preview.png");
            Check(!closed.Qr&&closed.Texts.Contains("서버 이전")&&closed.Texts.Contains("이 서버의 연결 링크")&&!closed.Texts.Contains("휴대폰 소식 확인 시각")&&closed.Texts.Any(t=>t.Contains("개발 진행 중",StringComparison.Ordinal)),"while the apps are in development the move dialog keeps the server move and closes the QR and phone settings");
            var open=MoveDialog(true,"school-link-preview.png");
            Check(open.Qr&&open.Texts.Contains("학교 연결 QR")&&open.Texts.Contains("휴대폰 소식 확인 시각")&&open.Texts.Contains("서버 이전"),"once the apps ship the same dialog offers the QR and the phone check time");
            var outsideDialog=MoveDialog(true,"external-preview.png",()=>ShowExternal(card));
            Check(outsideDialog.Texts.Contains("학교 밖 조회")&&outsideDialog.Texts.Contains(SimpleOperation.ExternalListener)&&outsideDialog.Texts.Any(t=>t.Contains("Cloudflare",StringComparison.Ordinal)),"the outside-access dialog names the connector target and says the traffic passes through Cloudflare");
            var moved=new SchoolAppSettings.Moving("https://192.0.2.10:18443/",new string('C',64),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+SchoolAppSettings.MoveNoticeMilliseconds);
            SchoolAppSettings.Write(card.SimpleData.Text,new(CheckTime:"07:40",Moving:moved));
            using(var api=new Api(address))
            {
                var app=(await api.Get<JsonElement>("server-info")).GetProperty("app");var refused=false;
                try{await api.Get<JsonElement>("api/session");}catch(ApiRequestException e)when(e.StatusCode==System.Net.HttpStatusCode.Gone){refused=true;}
                Check(app.GetProperty("checkTime").GetString()=="07:40"&&app.GetProperty("moving").GetProperty("address").GetString()==moved.Address&&refused,"a moved server tells apps and teacher PCs the new address and accepts nothing else");
            }
            SchoolAppSettings.Write(card.SimpleData.Text,new());
            using(var api=new Api(address))Check((await api.Get<JsonElement>("api/session")).ValueKind==JsonValueKind.Object,"cancelling the move puts the server back to normal");
            BuildChecklist(card);Check(card.Checklist.Children.Count>=5,"the dashboard lists the remaining operation checks instead of blocking start");
            SaveCapture(Path.Combine(outputDirectory,"server-manager-simple.png"));
            var first=card.Fingerprint;await card.Host.Stop();card.Password.Clear();card.Confirm.Clear();Refresh();
            Check(card.PasswordFields.IsVisible&&card.PasswordLabel?.Text=="새 관리자 비밀번호 · 바꿀 때만"&&!card.AdminUsername.IsEnabled,"after the first start the administrator ID is fixed and the password field only changes it");
            await Start(card);Check(await Ready()&&card.Fingerprint==first,"restarting keeps the same certificate so teachers never re-confirm the code");
            using(var after=new Api(address)){await after.Login("it.manager",password);Check((await after.Get<JsonElement>("api/admin/registrations")).EnumerateArray().Any(r=>r.GetProperty("username").GetString()=="new.teacher")&&(await after.Get<JsonElement>("api/registration/policy")).GetProperty("canRegister").GetBoolean(),"a restart keeps account applications and the confirmed terms; nobody signs up again");}
            using(var same=new Api(address)){await same.Login("it.manager",password);Check(same.Session?.User?.IsAdmin==true,"restarting with an empty password field keeps the administrator password");}
            await card.Host.Stop();
            var changed="Changed-"+Guid.NewGuid().ToString("N")[..12];card.Password.Password=card.Confirm.Password=changed;await Start(card);Check(await Ready(),"the server starts with a new administrator password");
            using(var renewed=new Api(address)){await renewed.Login("it.manager",changed);Check(renewed.Session?.User?.IsAdmin==true,"the new administrator password signs in");}
            using(var old=new Api(address)){var refused=false;try{await old.Login("it.manager",password);}catch(InvalidOperationException){refused=true;}Check(refused,"the old administrator password no longer signs in");}
            await card.Host.Stop();card.Auto.IsChecked=false;LaunchedAtSignIn=true;
            await StartSelected();Check(card.Host.Running&&await Ready(),"opened by Windows sign-in, the simple-setup server starts without the auto-start switch or a password");
            LaunchedAtSignIn=false;await card.Host.Stop();
            Check(new SchoolPinsProbe(address,"0000000000000000000000000000000000000000000000000000000000000000").Rejects(),"a different certificate for the same address is never accepted silently");
            File.WriteAllText(Path.Combine(outputDirectory,"simple-setup-checks.json"),JsonSerializer.Serialize(new{checks}));
        }
        catch(Exception error){File.WriteAllText(Path.Combine(outputDirectory,"server-manager-error.txt"),error.ToString()+"\n"+card.Host.Output);Environment.ExitCode=1;}
        finally
        {
            try{await card.Host.Stop();}catch(InvalidOperationException){}
            using var store=new X509Store(StoreName.My,StoreLocation.CurrentUser);store.Open(OpenFlags.ReadWrite);
            foreach(var certificate in store.Certificates.Find(X509FindType.FindByThumbprint,File.Exists(Path.Combine(directory,"certificate-office.txt"))?File.ReadAllText(Path.Combine(directory,"certificate-office.txt")).Trim():"-",false))store.Remove(certificate);
            quitting=true;Application.Current.Shutdown(Environment.ExitCode);
        }
    }
    void SaveCapture(string path)
    {
        UpdateLayout();var s=MainWindow.CaptureScale;var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth*s),(int)(ActualHeight*s),96*s,96*s,System.Windows.Media.PixelFormats.Pbgra32);image.Render(this);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    sealed record SchoolPinsProbe(string Address,string Other)
    {
        public bool Rejects()=>!CertificatePins.ValidateFingerprint(new Uri(Address+"/"),Other);
    }
}
