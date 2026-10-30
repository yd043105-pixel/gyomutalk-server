using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    bool findingServer;
    async void FindNearbyServerClick(object sender,RoutedEventArgs e)=>await FindSchoolServer();
    async Task FindSchoolServer()
    {
        if(findingServer||signingIn)return;findingServer=true;FindServerButton.IsEnabled=LoginButton.IsEnabled=false;
        LoginError.Text="교내 학교 서버를 찾는 중…";
        try
        {
            var candidates=await SchoolDiscovery.Search();
            if(candidates.Length==0){LoginError.Text="같은 망에서 학교 서버를 찾지 못했습니다. 서버 PC에서 교무톡 서버가 실행 중인지 확인하고, 관리자 프로그램의 ‘교사 PC 접속 주소’를 직접 입력하세요. 교사 PC와 서버 PC가 다른 망이면 자동검색이 되지 않습니다.";return;}
            var picker=new Window{Owner=this,Title="찾은 학교 서버 확인",Width=490,Height=400,Resources=Resources,Style=(Style)FindResource("SchoolWindow"),WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel{Margin=new Thickness(22)};picker.Content=panel;
            panel.Children.Add(new TextBlock{Text="서버 PC의 관리자 프로그램에 표시된 주소·연결 확인 코드와 같은 서버를 고르세요. 이름만으로는 학교 서버임이 보장되지 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            var list=new ListBox{ItemsSource=candidates.Select(c=>c.SchoolName+"\n"+c.Address+(c.Fingerprint.Length>0?"\n연결 확인 코드 "+CertificatePins.Code(c.Fingerprint):"")).ToArray(),SelectedIndex=0,Height=180};panel.Children.Add(list);
            var use=new Button{Content="이 서버 주소 사용",Margin=new Thickness(0,15,0,0)};panel.Children.Add(use);
            use.Click+=(_,_)=>{if(list.SelectedIndex<0)return;var selected=candidates[list.SelectedIndex];ServerAddress.Text=selected.Address.TrimEnd('/');SavePreferences();picker.DialogResult=true;};
            LoginError.Text=picker.ShowDialog()==true?"학교 서버 주소를 저장했습니다. 본인 이름으로 로그인하세요.":"서버 선택을 취소했습니다.";
        }
        catch(Exception e){LoginError.Text="자동검색 실패: "+e.Message;}
        finally{findingServer=false;FindServerButton.IsEnabled=LoginButton.IsEnabled=true;}
    }
}
