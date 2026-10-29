using System.Text.Json;
using System.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Desktop;
public partial class RegistrationWindow : Window
{
    readonly Api api;
    bool sending;
    RegistrationPolicySet? policy;
    internal readonly Dictionary<string,CheckBox> PolicyChoices=new();
    internal Task PolicyReady{get;}
    public RegistrationWindow(string address)
    {
        api = new Api(address); InitializeComponent();
        Closed += (_, _) => { PasswordBox.Clear(); ConfirmBox.Clear(); api.Dispose(); };
        PolicyReady=LoadPolicies();
    }
    async Task LoadPolicies()
    {
        try
        {
            policy=await api.Get<RegistrationPolicySet>("api/registration/policy");
            PolicyPanel.Children.Add(new TextBlock{Text=policy.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)});
            foreach(var document in policy.Documents)
            {
                var label=(document.Required?"필수 · ":"선택 · ")+document.Title+(document.Action=="consent"?"에 동의합니다.":"를 확인했습니다.");
                var check=new CheckBox{IsEnabled=false,Content=new TextBlock{Text=label,TextWrapping=TextWrapping.Wrap},Margin=new Thickness(0,6,0,6)};
                PolicyChoices.Add(document.Id,check);PolicyPanel.Children.Add(check);
                var open=new Button{Content="전문 보기 · "+document.Title,Margin=new Thickness(0,0,0,8)};
                open.Click+=(_,_)=>
                {
                    ShowDocument(this,policy,document);check.IsEnabled=policy.CanRegister;
                };PolicyPanel.Children.Add(open);
            }
            SubmitButton.IsEnabled=policy.CanRegister;
        }
        catch(Exception error){Feedback.Text="약관을 불러오지 못했습니다. 창을 다시 열어주세요. "+error.Message;}
    }
    internal static void ShowDocument(Window owner,RegistrationPolicySet policy,RegistrationDocument document)
    {
        new Window{Owner=owner,Title=document.Title,Width=520,Height=560,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new TextBox{Text=policy.Message+"\n버전 · "+document.Version+"\n운영 · "+policy.Operator+"\n문의 · "+policy.Contact+"\n\n"+document.Body,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(18)}}.ShowDialog();
    }
    async void SubmitClick(object sender, RoutedEventArgs e)
    {
        if (sending) return;
        if (PasswordBox.Password != ConfirmBox.Password) { Feedback.Text = "비밀번호 확인이 일치하지 않습니다."; return; }
        sending = true; SubmitButton.IsEnabled = false;
        try
        {
            if(policy is null||!policy.CanRegister)throw new InvalidOperationException(policy?.Message??"약관을 먼저 불러와야 합니다.");
            if(policy.Documents.Any(d=>d.Required&&PolicyChoices[d.Id].IsChecked!=true))throw new InvalidOperationException("필수 약관·개인정보 안내의 전문을 확인하고 체크하세요.");
            var agreement=new RegistrationAcceptance(policy.Revision,PolicyChoices.Where(c=>c.Value.IsChecked==true).Select(c=>c.Key).ToArray());
            var response = await api.Send<JsonElement>(HttpMethod.Post, "api/register", new { username = UsernameBox.Text.Trim(), name = NameBox.Text.Trim(), department = DepartmentBox.Text.Trim(), password = PasswordBox.Password,agreement });
            PasswordBox.Clear(); ConfirmBox.Clear(); Feedback.Text = response.GetProperty("message").GetString();
            SubmitButton.Content = "신청 완료 · 창을 닫고 승인 후 로그인하세요";
        }
        catch (Exception error) { Feedback.Text = error.Message; SubmitButton.IsEnabled = true; }
        finally { sending = false; }
    }
}
