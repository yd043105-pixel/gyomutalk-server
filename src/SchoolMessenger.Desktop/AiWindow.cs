using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SchoolMessenger.Desktop;

// 설정 → AI 연결: each teacher connects their own ChatGPT account, OpenRouter key or Google AI Studio key on this PC.
public sealed class AiWindow : Window
{
    readonly StackPanel stack = new() { Margin = new Thickness(24) };
    readonly Dictionary<string, string[]> models = [];
    readonly HashSet<string> loading = [];
    CancellationTokenSource? signing;
    string? problem, busy;
    readonly string? reason;

    public AiWindow(Window owner, string? reason = null)
    {
        this.reason = reason;
        Owner = owner; Title = "AI 연결"; Width = 520; Height = Math.Min(760, SystemParameters.WorkArea.Height - 24); MinWidth = 440; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Style = (Style)FindResource("SchoolWindow");
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closed += (_, _) => signing?.Cancel();
        Draw();
        foreach (var provider in Ai.Providers.Where(Ai.Current.Connected)) _ = LoadModels(provider);
    }

    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    void Draw()
    {
        stack.Children.Clear();
        stack.Children.Add(AdminUi.Heading("AI 연결", 22));
        var intro = AdminUi.Label("메시지의 ‘AI로 다듬기’와 수강신청 분반 편성의 ‘AI로 설명 듣기’에 씁니다. 선생님 본인의 계정이나 열쇠를 이 PC에 연결합니다.", "Muted", 13);
        intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 6, 0, 16); stack.Children.Add(intro);
        if (reason is not null && !Ai.Ready)
        {
            var why = AdminUi.Label(reason, "Blue", 13, true); why.TextWrapping = TextWrapping.Wrap;
            var banner = new Border { Child = why, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 16) };
            banner.SetResourceReference(Border.BackgroundProperty, "Pale"); stack.Children.Add(banner);
        }

        var connected = Ai.Providers.Where(Ai.Current.Connected).ToArray();
        if (connected.Length > 0)
        {
            stack.Children.Add(AdminUi.Group("다듬기에 쓸 AI"));
            var use = new ComboBox { ItemsSource = connected.Select(Ai.Name).ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(connected, Ai.Current.Use)), Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetName(use, "다듬기에 쓸 AI");
            use.SelectionChanged += (_, _) => { if (use.SelectedIndex >= 0 && connected[use.SelectedIndex] != Ai.Current.Use) { Ai.Save(Ai.Current with { Use = connected[use.SelectedIndex] }); Draw(); } };
            stack.Children.Add(use);
        }
        if (problem is not null) { var p = AdminUi.Label(problem, "Danger", 12); p.TextWrapping = TextWrapping.Wrap; p.Margin = new Thickness(0, 0, 0, 12); stack.Children.Add(p); }

        stack.Children.Add(Card(Ai.ChatGpt, "ChatGPT Plus·Pro 계정으로 로그인하면 그 플랜의 사용량으로 씁니다. 따로 내는 돈은 없습니다(무료 계정은 쓸 수 없습니다).", null));
        stack.Children.Add(Card(Ai.OpenRouter, "열쇠 하나로 여러 회사 모델(Claude, Gemini, Llama 등)을 씁니다. 이름이 ‘:free’로 끝나는 모델은 무료이고, 나머지는 충전한 만큼 씁니다.", Ai.OpenRouterKeys));
        stack.Children.Add(Card(Ai.Google, "구글 계정으로 무료 열쇠를 받습니다. 무료 사용분은 하루 한도가 있고, 구글이 입력한 글을 서비스 개선에 쓸 수 있습니다.", Ai.GoogleKeys));

        var note = AdminUi.Label("로그인 정보와 열쇠는 이 PC에만 암호화해 저장하고 학교 서버로 보내지 않습니다. ‘AI로 다듬기’를 누르면 그 글이 고른 AI 회사로 전송되니 학생 이름·연락처 같은 개인정보는 빼고 쓰세요.", "Faint", 12);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 4, 0, 16); stack.Children.Add(note);
        var done = new Button { Content = "완료", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left }; done.Click += (_, _) => Close(); stack.Children.Add(done);
    }

    Border Card(string provider, string about, string? keyPage)
    {
        var box = new StackPanel();
        var account = Ai.Current.Of(provider); var on = Ai.Current.Connected(provider);
        var top = new DockPanel();
        var badge = AdminUi.Badge(on ? "연결됨" : "연결 안 됨", on ? "ok" : "muted"); DockPanel.SetDock(badge, Dock.Right); badge.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(badge); top.Children.Add(AdminUi.Heading(Ai.Name(provider), 16)); box.Children.Add(top);
        var text = AdminUi.Label(about, "Muted", 12); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 6, 0, 12); box.Children.Add(text);
        var actions = new WrapPanel(); box.Children.Add(actions);
        Button Action(string label, string name, Func<Task> run, bool primary = false)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), IsEnabled = busy is null };
            if (primary) button.Style = (Style)FindResource("PrimaryButton");
            AutomationProperties.SetName(button, name);
            button.Click += async (_, _) =>
            {
                busy = provider; problem = null; Draw();
                try { await run(); }
                catch (OperationCanceledException) { }
                catch (Exception e) when (e is AiException or System.IO.IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { problem = e.Message; }
                finally { busy = null; signing = null; Draw(); }
            };
            actions.Children.Add(button); return button;
        }
        if (provider == Ai.ChatGpt)
        {
            if (busy == provider && signing is not null)
            {
                actions.Children.Add(new TextBlock { Text = "브라우저에서 ChatGPT 로그인을 마치세요…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) });
                var cancel = new Button { Content = "취소", Margin = new Thickness(0, 0, 8, 8) }; cancel.Click += (_, _) => signing?.Cancel(); actions.Children.Add(cancel);
            }
            else if (!on) Action("Continue with ChatGPT", "Continue with ChatGPT · 브라우저에서 로그인", async () =>
            {
                signing = new CancellationTokenSource(); Draw();
                await Ai.SignIn(url => { if (Environment.GetEnvironmentVariable("YY_SCREEN_TOUR") is null) Open(url); }, signing.Token);
                if (!Ai.Current.Welcomed)
                {
                    Notice.Show(this, "You're using your ChatGPT plan\n\n이 프로그램에서 하는 AI 요청은 선생님의 ChatGPT 플랜 사용량으로 처리됩니다. 사용량은 ChatGPT 설정에서 관리합니다.", "ChatGPT 연결");
                    Ai.Save(Ai.Current with { Welcomed = true });
                }
                await LoadModels(provider);
            }, true);
            else
            {
                if (account.Email is { } email) box.Children.Insert(2, Small(email + " 계정으로 연결됨"));
                Action("로그아웃", "ChatGPT 로그아웃", () => { Ai.SignOut(provider); models.Remove(provider); return Task.CompletedTask; });
            }
        }
        else if (!on)
        {
            Action("키 받기", Ai.Name(provider) + " 키 받기 · 브라우저에서 열기", () => { Open(keyPage!); return Task.CompletedTask; });
            var key = new PasswordBox { Margin = new Thickness(0, 0, 0, 8), MinHeight = 34 }; AutomationProperties.SetName(key, Ai.Name(provider) + " 열쇠");
            box.Children.Add(Small("받은 열쇠를 붙여넣고 ‘연결’을 누르세요.")); box.Children.Add(key);
            var connect = new WrapPanel(); box.Children.Add(connect);
            var go = new Button { Content = "연결", Style = (Style)FindResource("PrimaryButton"), IsEnabled = busy is null, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(go, Ai.Name(provider) + " 연결");
            go.Click += async (_, _) =>
            {
                busy = provider; problem = null; var value = key.Password; Draw();
                try { models[provider] = await Ai.Connect(provider, value, CancellationToken.None); Choose(provider); }
                catch (AiException e) { problem = e.Message; }
                finally { busy = null; Draw(); }
            };
            connect.Children.Add(go);
            if (busy == provider) connect.Children.Add(new TextBlock { Text = "열쇠를 확인하는 중…", VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            box.Children.Insert(2, Small("열쇠 " + Masked(account.Key!) + " 로 연결됨"));
            Action("연결 끊기", Ai.Name(provider) + " 연결 끊기", () => { Ai.SignOut(provider); models.Remove(provider); return Task.CompletedTask; });
        }
        if (on && busy is null) box.Children.Add(ModelPicker(provider, account));
        var shell = AdminUi.Card(box, 18); shell.Margin = new Thickness(0, 0, 0, 12); return shell;
    }

    UIElement ModelPicker(string provider, Ai.Account account)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(Small("모델"));
        if (!models.TryGetValue(provider, out var list)) { panel.Children.Add(Small(loading.Contains(provider) ? "모델 목록을 불러오는 중…" : "모델 목록을 불러오지 못했습니다.")); return panel; }
        var pick = new ComboBox { ItemsSource = list, SelectedItem = account.Model, IsTextSearchEnabled = true, MaxDropDownHeight = 320 };
        AutomationProperties.SetName(pick, Ai.Name(provider) + " 모델");
        pick.SelectionChanged += (_, _) => { if (pick.SelectedItem is string model && model != Ai.Current.Of(provider).Model) Ai.Save(Ai.Current.With(provider, Ai.Current.Of(provider) with { Model = model })); };
        panel.Children.Add(pick);
        if (provider == Ai.ChatGpt)
        {
            var line = new TextBlock { Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
            line.Inlines.Add(new Run("Using ChatGPT plan · "));
            var manage = new Hyperlink(new Run("Manage usage")); manage.Click += (_, _) => Open(Ai.ManageUsage); line.Inlines.Add(manage);
            line.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(line);
        }
        return panel;
    }

    async Task LoadModels(string provider)
    {
        loading.Add(provider); Draw();
        try { models[provider] = await Ai.Models(provider, CancellationToken.None); Choose(provider); }
        catch (AiException e) { problem = e.Message; }
        finally { loading.Remove(provider); Draw(); }
    }

    // A model is picked for the teacher so ‘AI로 다듬기’ works right after connecting: a fast, cheap one where it can tell.
    void Choose(string provider)
    {
        var account = Ai.Current.Of(provider);
        if (!models.TryGetValue(provider, out var list) || list.Length == 0 || account.Model is { } m && list.Contains(m)) return;
        var model = provider switch
        {
            Ai.Google => list.FirstOrDefault(id => id.Contains("flash", StringComparison.OrdinalIgnoreCase) && !id.Contains("lite", StringComparison.OrdinalIgnoreCase) && !id.Contains("preview", StringComparison.OrdinalIgnoreCase)),
            Ai.OpenRouter => list.FirstOrDefault(id => id.EndsWith(":free", StringComparison.Ordinal)),
            _ => list.FirstOrDefault(id => id.Contains("mini", StringComparison.OrdinalIgnoreCase))
        } ?? list[0];
        Ai.Save(Ai.Current.With(provider, account with { Model = model }));
    }

    static string Masked(string key) => key.Length <= 8 ? "••••" : key[..Math.Min(6, key.Length - 4)] + "…" + key[^4..];
    static TextBlock Small(string text) { var t = AdminUi.Label(text, "Muted", 12); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 0, 0, 6); return t; }
}
