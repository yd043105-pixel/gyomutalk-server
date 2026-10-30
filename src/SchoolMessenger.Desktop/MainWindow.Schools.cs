using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    string connectedAddress="";
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
