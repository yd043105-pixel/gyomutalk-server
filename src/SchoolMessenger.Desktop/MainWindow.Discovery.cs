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
            if(candidates.Length==0){LoginError.Text="학교 서버를 찾지 못했습니다. 교직원망 연결, 서버의 자동검색 설정·인증서·방화벽을 확인하거나 주소를 직접 입력하세요.";return;}
            var picker=new Window{Owner=this,Title="찾은 학교 서버 확인",Width=490,Height=370,Resources=Resources,Style=(Style)FindResource("SchoolWindow"),WindowStartupLocation=WindowStartupLocation.CenterOwner};
            var panel=new StackPanel{Margin=new Thickness(22)};picker.Content=panel;
            panel.Children.Add(new TextBlock{Text="학교에서 안내한 서버 주소와 일치하는지 확인하세요. 검색 결과의 이름만으로 학교 서버임이 보장되지는 않습니다.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});
            var list=new ListBox{ItemsSource=candidates.Select(c=>c.SchoolName+"\n"+c.Address).ToArray(),SelectedIndex=0,Height=150};panel.Children.Add(list);
            var use=new Button{Content="이 서버 주소 사용",Margin=new Thickness(0,15,0,0)};panel.Children.Add(use);
            use.Click+=(_,_)=>{if(list.SelectedIndex<0)return;var selected=candidates[list.SelectedIndex];if(ServerAddress.Text.TrimEnd('/')!=selected.Address.TrimEnd('/'))savedLogin.Clear();ServerAddress.Text=selected.Address.TrimEnd('/');SavePreferences();picker.DialogResult=true;};
            LoginError.Text=picker.ShowDialog()==true?"학교 서버 주소를 저장했습니다. 본인 이름으로 로그인하세요.":"서버 선택을 취소했습니다.";
        }
        catch(Exception e){LoginError.Text="자동검색 실패: "+e.Message;}
        finally{findingServer=false;FindServerButton.IsEnabled=LoginButton.IsEnabled=true;}
    }
}
