using System;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;

// The first sign-in with the initial password: the person sets a password of their own before anything else opens.
public sealed class StaffActivationWindow:Window
{
    public StaffActivationWindow(Api api)
    {
        Title="교무톡 · 첫 설정";Width=420;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var stack=new StackPanel{Margin=new Thickness(24)};AdminUi.Page(this,stack);
        stack.Children.Add(AdminUi.Heading("개인 비밀번호 정하기",22));
        var intro=AdminUi.Label("처음 로그인하셨습니다. 앞으로 쓸 비밀번호를 정하면 교무톡을 쓸 수 있습니다.","Muted",13);intro.Margin=new Thickness(0,8,0,18);stack.Children.Add(intro);
        PasswordBox Field(string label)
        {
            var caption=AdminUi.Label(label,"Muted",12);caption.Margin=new Thickness(0,0,0,6);stack.Children.Add(caption);
            var box=new PasswordBox{MaxLength=128,Margin=new Thickness(0,0,0,14)};System.Windows.Automation.AutomationProperties.SetName(box,label);stack.Children.Add(box);return box;
        }
        var password=Field("새 비밀번호 · 8~128자");var confirm=Field("비밀번호 확인");
        var feedback=AdminUi.Label("","Danger",12);feedback.Margin=new Thickness(0,0,0,12);feedback.Visibility=Visibility.Collapsed;stack.Children.Add(feedback);
        var save=new Button{Content="첫 설정 마치기",IsDefault=true};save.SetResourceReference(StyleProperty,"PrimaryButton");stack.Children.Add(save);
        var busy=false;Closed+=(_,_)=>{password.Clear();confirm.Clear();};Loaded+=(_,_)=>password.Focus();
        save.Click+=async(_,_)=>
        {
            if(busy)return;busy=true;save.IsEnabled=false;feedback.Visibility=Visibility.Collapsed;
            try
            {
                if(password.Password!=confirm.Password)throw new InvalidOperationException("비밀번호 확인이 일치하지 않습니다.");
                var agreement=await RegistrationWindow.OlderServerAgreement(api,this);
                await api.Send<JsonElement>(HttpMethod.Post,"api/activate",new{newPassword=password.Password,agreement});
                Notice.Show(this,"설정을 마쳤습니다. 방금 정한 비밀번호로 다시 로그인하세요.","첫 설정");DialogResult=true;
            }
            catch(Exception e){feedback.Text=Api.Explain(e);feedback.Visibility=Visibility.Visible;}finally{password.Clear();confirm.Clear();busy=false;save.IsEnabled=true;}
        };
    }
}
