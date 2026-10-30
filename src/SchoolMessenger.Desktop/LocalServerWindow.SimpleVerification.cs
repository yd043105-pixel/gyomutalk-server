using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

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
            card.School.Text="가상고등학교";card.AdminUsername.Text="it.manager";
            var approval=Path.Combine(directory,"synthetic-approval.pdf");File.WriteAllText(approval,"synthetic principal approval for verification");
            var (kept,hash)=SimpleOperation.KeepApproval(directory,"office",approval);card.Approval.Text=kept;card.ApprovalSha256=hash;card.ReviewedAt=DateTimeOffset.Now;
            card.Password.Password=card.Confirm.Password=password;SelectCard(card);Refresh();UpdateLayout();
            Check(card.SimpleFields.IsVisible&&card.PasswordFields.IsVisible&&!card.TrialFields.IsVisible&&!card.ProductionFields.IsVisible,"simple setup asks only for school name, administrator account and approval document");
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
                Check(info.GetProperty("schoolName").GetString()=="가상고등학교","the school name identifies the server for teacher PCs");
            }
            using(var wrong=new Api(address)){var rejected=false;try{await wrong.Login("admin",password);}catch(InvalidOperationException){rejected=true;}Check(rejected,"the default admin ID is not created when another ID is chosen");}
            var discovery=JsonDocument.Parse(File.ReadAllText(Path.Combine(card.SimpleData.Text,"discovery.json"))).RootElement;
            Check(discovery.GetProperty("enabled").GetBoolean()&&discovery.GetProperty("address").GetString()!.StartsWith("https://",StringComparison.Ordinal),"teacher PC discovery is turned on with the HTTPS address");
            BuildChecklist(card);Check(card.Checklist.Children.Count>=5,"the dashboard lists the remaining operation checks instead of blocking start");
            SaveCapture(Path.Combine(outputDirectory,"server-manager-simple.png"));
            var first=card.Fingerprint;await card.Host.Stop();card.Password.Clear();card.Confirm.Clear();Refresh();
            Check(!card.PasswordFields.IsVisible&&!card.AdminUsername.IsEnabled,"after the first start the password and administrator ID are not asked again");
            await Start(card);Check(await Ready()&&card.Fingerprint==first,"restarting keeps the same certificate so teachers never re-confirm the code");
            await card.Host.Stop();
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
        UpdateLayout();var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(this);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using var file=File.Create(path);encoder.Save(file);
    }
    sealed record SchoolPinsProbe(string Address,string Other)
    {
        public bool Rejects()=>!CertificatePins.ValidateFingerprint(new Uri(Address+"/"),Other);
    }
}
