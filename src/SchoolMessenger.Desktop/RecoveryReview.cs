using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

public static class RecoveryReview
{
    public static async Task Add(StackPanel panel,Api api,Window owner,Func<Task> reload)
    {
        var state=await api.Get<JsonElement>("api/admin/recovery-status");
        if(!state.GetProperty("reviewRequired").GetBoolean())return;
        panel.Children.Add(new TextBlock{Text="백업 복원 후 이용이 일시 중지되어 있습니다. 현재 재학·재직 명부, 보호자 관계와 권한을 대조하고 필요한 변경을 저장하세요.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,8)});
        var button=new Button{Content="복원 검토 완료 · 일반 이용 재개",Margin=new Thickness(0,4,0,12)};panel.Children.Add(button);
        button.Click+=async(_,_)=>
        {
            if(MessageBox.Show(owner,"백업 이후 퇴직·전학·졸업·보호자 연결 해제·권한 변경을 현재 학교 기록과 대조하고 반영했습니까? 확인 후 일반 이용을 다시 엽니다.","복원 후 권한 확인",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
            button.IsEnabled=false;
            try{await api.Send<JsonElement>(HttpMethod.Post,"api/admin/recovery-confirm",new{confirmed=true});await reload();}
            catch(Exception error){MessageBox.Show(owner,error.Message,"복원 확인");button.IsEnabled=true;}
        };
    }
}
