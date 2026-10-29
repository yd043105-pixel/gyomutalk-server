using System.Threading;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    SchoolDirectory.Entry? selectedSchool;
    void RestoreSchoolSelection(Preferences preferences)
    {
        if(preferences.SchoolId.Length>0&&preferences.SchoolLabel.Length>0)
        {
            selectedSchool=new(preferences.SchoolId,preferences.SchoolLabel,preferences.SchoolRegion,preferences.ServerAddress,"");
            SchoolSelectionLabel.Text=selectedSchool.Label;
        }
        else if(preferences.ServerAddress.Length>0)SchoolSelectionLabel.Text="기존 학교 연결 · 학교 검색에서 변경";
    }
    void ConnectionAddressChanged(object sender,TextChangedEventArgs e)
    {
        if(!loginOptionsReady)return;
        if(selectedSchool is not null&&ServerAddress.Text.TrimEnd('/')==selectedSchool.OfficeAddress.TrimEnd('/'))return;
        selectedSchool=null;SchoolSelectionLabel.Text="학교를 검색해 선택하세요";savedLogin.Clear();PasswordBox.Clear();
    }
    Task VerifySelectedSchool()=>selectedSchool is null
        ?string.IsNullOrWhiteSpace(ServerAddress.Text)?Task.FromException(new InvalidOperationException("학교를 먼저 검색해 선택하세요.")):Task.CompletedTask
        :SchoolDirectory.Verify(selectedSchool,"office");
    void SelectSchool(SchoolDirectory.Entry school)
    {
        var changed=selectedSchool?.Id!=school.Id||ServerAddress.Text.TrimEnd('/')!=school.OfficeAddress.TrimEnd('/');
        if(changed){savedLogin.Clear();PasswordBox.Clear();RememberLogin.IsChecked=false;Favorites.Clear();}
        ServerAddress.Text=school.OfficeAddress.TrimEnd('/');selectedSchool=school;SchoolSelectionLabel.Text=school.Label;SavePreferences();
    }
    void FindServerClick(object sender,RoutedEventArgs e)
    {
        if(findingServer||signingIn)return;findingServer=true;FindServerButton.IsEnabled=LoginButton.IsEnabled=false;
        using var stop=new CancellationTokenSource();
        var picker=new Window{Owner=this,Title="우리 학교 찾기",Width=500,Height=510,MinWidth=420,MinHeight=470,Style=(Style)FindResource("SchoolWindow"),WindowStartupLocation=WindowStartupLocation.CenterOwner};
        var panel=new StackPanel{Margin=new Thickness(22)};picker.Content=panel;
        panel.Children.Add(new TextBlock{Text="우리 학교 찾기",FontSize=22,FontWeight=FontWeights.Bold,Margin=new Thickness(0,0,0,8)});
        panel.Children.Add(new TextBlock{Text="학교명 또는 지역으로 검색하고, 같은 이름이면 지역을 확인하세요.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
        var query=new TextBox{MaxLength=80,ToolTip="학교명 또는 지역",Margin=new Thickness(0,0,0,10)};panel.Children.Add(query);
        var list=new ListBox{Height=190,DisplayMemberPath="Label"};panel.Children.Add(list);
        var status=new TextBlock{Text="등록된 학교 목록을 불러오는 중…",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,10)};panel.Children.Add(status);
        var use=new Button{Content="이 학교 선택",IsEnabled=false};panel.Children.Add(use);
        SchoolDirectory.Catalog catalog=new("",[]);
        void Filter(){list.ItemsSource=SchoolDirectory.Search(catalog.Schools,query.Text,"office");use.IsEnabled=list.SelectedItem is SchoolDirectory.Entry;}
        query.TextChanged+=(_,_)=>Filter();list.SelectionChanged+=(_,_)=>use.IsEnabled=list.SelectedItem is SchoolDirectory.Entry;
        picker.Closed+=(_,_)=>stop.Cancel();
        picker.Loaded+=async(_,_)=>
        {
            try
            {
                catalog=SchoolDirectory.Bundled();Filter();
                catalog=await SchoolDirectory.Refresh(catalog,stop.Token);if(stop.IsCancellationRequested)return;Filter();
                status.Text=catalog.Schools.Length==0?"아직 등록된 학교가 없습니다. 학교 관리자에게 학교 등록 여부를 확인하세요.":"최대 100개 표시 · 검색어로 목록을 좁힐 수 있습니다.";
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            catch{if(!stop.IsCancellationRequested)status.Text="최신 목록에 연결하지 못했습니다. 포함된 목록 또는 이전 학교 연결을 이용하고, 학교에 등록 여부를 확인하세요.";}
            query.Focus();
        };
        use.Click+=async(_,_)=>
        {
            if(list.SelectedItem is not SchoolDirectory.Entry school)return;use.IsEnabled=false;query.IsEnabled=list.IsEnabled=false;
            try
            {
                status.Text="학교 연결을 확인하는 중…";await SchoolDirectory.Verify(school,"office",stop.Token);
                if(stop.IsCancellationRequested)return;
                SelectSchool(school);
                LoginError.Text="학교를 선택했습니다. 본인 이름으로 로그인하세요.";picker.DialogResult=true;
            }
            catch(OperationCanceledException)when(stop.IsCancellationRequested){}
            catch{status.Text="학교에 연결하지 못했거나 등록 정보가 다릅니다. 교내망/VPN 연결과 학교 서버 가동 여부를 확인하세요.";}
            finally{if(!stop.IsCancellationRequested){query.IsEnabled=list.IsEnabled=true;use.IsEnabled=true;}}
        };
        try{picker.ShowDialog();}finally{findingServer=false;FindServerButton.IsEnabled=LoginButton.IsEnabled=true;}
    }
}
