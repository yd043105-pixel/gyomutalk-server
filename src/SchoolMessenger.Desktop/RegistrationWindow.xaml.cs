using System.Text.Json;
using System.Windows;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;
public partial class RegistrationWindow : Window
{
    readonly Api api;
    bool sending;
    public RegistrationWindow(string address)
    {
        api = new Api(address); InitializeComponent(); StatusTone.Watch(Feedback); Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);
        Closed += (_, _) => { PasswordBox.Clear(); ConfirmBox.Clear(); api.Dispose(); };
    }
    // Programs and servers are updated on different days. A school server older than 0.1.3 still wants agreement to
    // its terms before a sign-up or a first setup, so against such a server its documents are shown once and the
    // agreement is sent along. A current server has no documents and nothing is asked.
    internal static async Task<object?> OlderServerAgreement(Api api,Window owner)
    {
        var policy=await api.Get<JsonElement>("api/registration/policy");var documents=policy.GetProperty("documents").EnumerateArray().Where(d=>d.GetProperty("required").GetBoolean()).ToArray();
        if(documents.Length==0)return null;
        if(!policy.GetProperty("canRegister").GetBoolean())throw new InvalidOperationException(policy.GetProperty("message").GetString());
        var panel=new StackPanel{Margin=new Thickness(24)};panel.Children.Add(AdminUi.Label("이 학교 서버는 이전 버전이라, 아래 안내에 동의해야 계속할 수 있습니다.","Ink",13));
        panel.Children.Add(new TextBox{Text=string.Join(Environment.NewLine+Environment.NewLine,documents.Select(d=>"■ "+d.GetProperty("title").GetString()+Environment.NewLine+d.GetProperty("body").GetString())),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=320,Margin=new Thickness(0,12,0,14)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button{Content="취소",IsCancel=true,Margin=new Thickness(0,0,8,0)};var agree=new Button{Content="동의하고 계속",IsDefault=true};agree.SetResourceReference(StyleProperty,"PrimaryButton");
        buttons.Children.Add(cancel);buttons.Children.Add(agree);panel.Children.Add(buttons);
        var dialog=new Window{Owner=owner,Title="학교 약관·개인정보 안내",Width=520,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel,Style=(Style)Application.Current.FindResource("SchoolWindow")};
        agree.Click+=(_,_)=>dialog.DialogResult=true;
        if(dialog.ShowDialog()!=true)throw new InvalidOperationException("동의하지 않아 진행하지 않았습니다.");
        return new{revision=policy.GetProperty("revision").GetString(),acceptedIds=documents.Select(d=>d.GetProperty("id").GetString()).ToArray()};
    }
    async void SubmitClick(object sender, RoutedEventArgs e)
    {
        if (sending) return;
        if (PasswordBox.Password != ConfirmBox.Password) { Feedback.Text = "비밀번호 확인이 일치하지 않습니다."; return; }
        sending = true; SubmitButton.IsEnabled = false;
        try
        {
            var agreement = await OlderServerAgreement(api, this);
            var response = await api.Send<JsonElement>(HttpMethod.Post, "api/register", new { username = NameBox.Text.Trim(), department = DepartmentBox.Text.Trim(), password = PasswordBox.Password, agreement });
            PasswordBox.Clear(); ConfirmBox.Clear(); Feedback.Text = response.GetProperty("message").GetString();
            SubmitButton.Content = "신청 완료 · 창을 닫고 승인 후 로그인하세요";
        }
        catch (Exception error) { Feedback.Text = Api.Explain(error); SubmitButton.IsEnabled = true; }
        finally { sending = false; }
    }
}
