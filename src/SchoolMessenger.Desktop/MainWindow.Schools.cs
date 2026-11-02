using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    string connectedAddress="";
    SchoolPicker schoolPick=null!;
    // Picking the school is enough on the school network: its server is found by name and address.
    async void SchoolPicked()
    {
        if(!loginOptionsReady||signingIn||findingServer||schoolPick.Selected is not {} school)return;
        await FindSchoolServer(school);
    }
    // The saved server moved (it says where), or it has a new address on the school network: follow it to the same
    // certificate, never to a different one.
    async Task<bool> FindMovedServer()
    {
        var old=ServerAddress.Text.Trim().TrimEnd('/');if(old.Length==0)return false;
        try
        {
            using var probe=new Api(old);var info=await probe.Get<JsonElement>("server-info");
            if(info.TryGetProperty("app",out var app)&&app.TryGetProperty("moving",out var move)&&move.ValueKind==JsonValueKind.Object)
            {
                var address=move.GetProperty("address").GetString();var fingerprint=move.GetProperty("fingerprint").GetString()??"";
                if(SchoolAppSettings.ValidMove(address,fingerprint))
                {
                    if(fingerprint.Length==64)CertificatePins.Trust(address!,fingerprint);
                    UseAddress(address!,"학교 서버가 새 주소로 이전되어 새 주소로 연결합니다.");return true;
                }
            }
        }
        catch(Exception e)when(e is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException or KeyNotFoundException){}
        if(CertificatePins.Pinned(old) is not {} pinned)return false;
        var found=(await SchoolDiscovery.Search()).FirstOrDefault(c=>c.Fingerprint.Equals(pinned,StringComparison.OrdinalIgnoreCase)&&c.Address.TrimEnd('/')!=old);
        if(found is null)return false;
        CertificatePins.Trust(found.Address,pinned);UseAddress(found.Address,"학교망에서 같은 서버를 새 주소로 찾아 연결합니다.");return true;
    }
    // The address changes for the same school, so the saved sign-in and favourites stay.
    void UseAddress(string address,string note){connectedAddress=address.TrimEnd('/');ServerAddress.Text=connectedAddress;LoginMessage(note,false);SavePreferences();}
    // The chosen school only confirms the address: a server stating another school (by name and address) is refused.
    async Task CheckChosenSchool()
    {
        if(schoolPick.Selected is not {} chosen)return;
        var info=await Api!.Get<SchoolDiscovery.Identity>("server-info");
        if(SchoolList.Mismatch(chosen,info.SchoolName,info.SchoolAddress) is {} problem)throw new InvalidOperationException(problem);
    }
    // Each school keeps its own sign-in and favourites, so moving to another address starts clean.
    void ConnectionAddressChanged(object sender,TextChangedEventArgs e)
    {
        if(!loginOptionsReady||ServerAddress.Text.Trim().TrimEnd('/')==connectedAddress.TrimEnd('/'))return;
        savedLogin.Clear();PasswordBox.Clear();RememberLogin.IsChecked=false;Favorites.Clear();connectedAddress=ServerAddress.Text.Trim();
    }
    Task RequireServerAddress()=>string.IsNullOrWhiteSpace(ServerAddress.Text)
        ?Task.FromException(new InvalidOperationException("학교 서버 주소를 입력하거나 ‘교내 자동검색’을 누르세요."))
        :Task.CompletedTask;
}
