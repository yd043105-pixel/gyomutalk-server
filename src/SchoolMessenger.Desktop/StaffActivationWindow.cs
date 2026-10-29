using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

public sealed class StaffActivationWindow:Window
{
    public StaffActivationWindow(Api api)
    {
        Title="교직원 계정 · 첫 설정";Width=480;Height=690;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var stack=new StackPanel{Margin=new Thickness(24)};Content=new ScrollViewer{Content=stack};
        stack.Children.Add(new TextBlock{Text="관리자가 개별 전달한 활성화 번호와 개인 비밀번호를 설정하세요. 완료 전에는 업무 자료를 열 수 없습니다.",TextWrapping=TextWrapping.Wrap});
        stack.Children.Add(new TextBlock{Text="개별 활성화 번호",Margin=new Thickness(0,16,0,5)});var code=new TextBox();stack.Children.Add(code);
        var password=new PasswordBox();var confirm=new PasswordBox();
        {stack.Children.Add(new TextBlock{Text="새 개인 비밀번호 · 12~128자",Margin=new Thickness(0,16,0,5)});stack.Children.Add(password);stack.Children.Add(new TextBlock{Text="비밀번호 확인",Margin=new Thickness(0,12,0,5)});stack.Children.Add(confirm);}
        var feedback=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};var save=new Button{Content="계정 활성화",IsEnabled=false};bool busy=false;
        Closed+=(_,_)=>{password.Clear();confirm.Clear();};
        Loaded+=async(_,_)=>
        {
            try
            {
                var policy=await api.Get<RegistrationPolicySet>("api/registration/policy");var choices=new Dictionary<string,CheckBox>();
                stack.Children.Add(new TextBlock{Text=policy.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,8)});
                foreach(var document in policy.Documents)
                {
                    var check=new CheckBox{IsEnabled=false,Content=new TextBlock{Text=(document.Required?"필수 · ":"선택 · ")+document.Title,TextWrapping=TextWrapping.Wrap},Margin=new Thickness(0,8,0,4)};choices[document.Id]=check;stack.Children.Add(check);
                    var open=new Button{Content="전문 보기 · "+document.Title};open.Click+=(_,_)=>{RegistrationWindow.ShowDocument(this,policy,document);check.IsEnabled=policy.CanRegister;};stack.Children.Add(open);
                }
                stack.Children.Add(feedback);stack.Children.Add(save);save.IsEnabled=policy.CanRegister;
                save.Click+=async(_,_)=>
                {
                    if(busy)return;busy=true;save.IsEnabled=false;
                    try
                    {
                        if(password.Password!=confirm.Password)throw new InvalidOperationException("비밀번호 확인이 일치하지 않습니다.");
                        if(policy.Documents.Any(d=>d.Required&&choices[d.Id].IsChecked!=true))throw new InvalidOperationException("필수 안내의 전문을 확인하고 체크하세요.");
                        await api.Send<JsonElement>(HttpMethod.Post,"api/activate",new StaffActivation(code.Text.Trim(),password.Password,new RegistrationAcceptance(policy.Revision,choices.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToArray())));
                        MessageBox.Show(this,"설정 완료. 개인 비밀번호로 다시 로그인하세요.","계정 활성화");DialogResult=true;
                    }
                    catch(Exception e){feedback.Text=e.Message;}finally{password.Clear();confirm.Clear();busy=false;save.IsEnabled=policy.CanRegister;}
                };
            }
            catch(Exception e){stack.Children.Add(feedback);feedback.Text=e.Message;}
        };
    }
}
