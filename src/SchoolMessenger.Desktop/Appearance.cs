using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace SchoolMessenger.Desktop;

public static class Appearance
{
    public sealed record Palette(string Id, string Name, string Accent, string Dark, string Tint);
    public static readonly Palette[] Palettes =
    [
        new("blue", "클래식 블루", "#245BC6", "#182D50", "#EAF0FC"),
        new("green", "숲 그린", "#17745D", "#193D35", "#E3F1EB"),
        new("lavender", "라벤더", "#7050B0", "#34294C", "#EEE8F7"),
        new("rose", "로즈", "#AD4865", "#4D2935", "#F7E7ED"),
        new("slate", "슬레이트", "#506277", "#253341", "#E9EDF2")
    ];
    public static string Selected { get; private set; } = "blue";
    public static bool Glass { get; private set; } = true;
    public static void Apply(string? id, bool glass)
    {
        var palette = Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];
        Selected = palette.Id; Glass = glass;
        var resources = System.Windows.Application.Current.Resources;
        Color ColorOf(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        void Brush(string key, string hex) => resources[key] = new SolidColorBrush(ColorOf(hex));
        Brush("Blue", palette.Accent); Brush("Navy", palette.Dark); Brush("Pale", palette.Tint);
        Brush("Ink", "#182C47"); Brush("Muted", "#5A6B82"); Brush("Line", "#DFE7F1");
        Brush("Soft", "#F3F6FB"); Brush("Surface", "#FFFFFF");
        Brush("Glass", glass ? "#ECFFFFFF" : "#FFFFFF");
        Brush("GlassLine", glass ? "#FFFFFFFF" : "#DFE7F1"); Brush("OnAccent", "#FFFFFF");
        Brush("MessageSectionTint","#EAF0FC");Brush("SchoolSectionTint","#E3F1EB");
        resources["Canvas"] = glass
            ? new LinearGradientBrush(ColorOf(palette.Tint), ColorOf("#F8FAFD"), 55)
            : new SolidColorBrush(ColorOf("#F3F6FB"));
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "Surface", "Glass", "Canvas", "Soft", "Pale", "MessageSectionTint", "SchoolSectionTint" }) resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Ink", "Muted", "Navy" }) resources[key] = SystemColors.WindowTextBrush;
            resources["Blue"] = SystemColors.HighlightBrush;
            resources["OnAccent"] = SystemColors.HighlightTextBrush;
            resources["Line"] = resources["GlassLine"] = SystemColors.WindowTextBrush;
        }
    }
}

public sealed class AppearanceWindow : Window
{
    public AppearanceWindow(MainWindow main)
    {
        Owner = main; Title = "화면 · 자리비움 설정"; Width = 390; Height = Math.Min(590,SystemParameters.WorkArea.Height-24);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)FindResource("SchoolWindow");
        var stack = new StackPanel { Margin = new Thickness(24) }; Content = new ScrollViewer{Content=stack};
        stack.Children.Add(new TextBlock { Text = "나에게 편안한 교무실", FontSize = 21, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = "열려 있는 창에도 바로 적용됩니다.", Margin = new Thickness(0, 8, 0, 20) });
        var choices = new ComboBox { ItemsSource = Appearance.Palettes.Select(p => p.Name).ToArray(), SelectedIndex = Array.FindIndex(Appearance.Palettes, p => p.Id == Appearance.Selected) };
        AutomationProperties.SetName(choices, "메신저 색상 테마"); stack.Children.Add(choices);
        var preview = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 18, 0, 18), Padding = new Thickness(18), BorderThickness = new Thickness(1) };
        preview.SetResourceReference(Border.BackgroundProperty, "Glass"); preview.SetResourceReference(Border.BorderBrushProperty, "GlassLine");
        var content = new StackPanel(); preview.Child = content;
        var heading = new TextBlock { Text = "오늘의 교무실", FontSize = 16, FontWeight = FontWeights.Bold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Blue"); content.Children.Add(heading);
        content.Children.Add(new TextBlock { Text = "읽기 편한 글자와 은은한 색감", Margin = new Thickness(0, 8, 0, 14) });
        content.Children.Add(new Button { Content = "색상 미리보기", Style = (Style)FindResource("PrimaryButton"), IsHitTestVisible = false, Focusable = false }); stack.Children.Add(preview);
        var glass = new CheckBox { Content = "유리 느낌의 배경과 반투명 카드", IsChecked = Appearance.Glass };
        stack.Children.Add(glass);
        stack.Children.Add(new TextBlock { Text = "고대비 모드에서는 Windows의 화면 색상을 사용합니다.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        stack.Children.Add(new TextBlock{Text="자동 자리비움",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,8)});
        var minutes=new[]{2,5,10,15,30};var away=new ComboBox{ItemsSource=minutes.Select(m=>m+"분 동안 입력이 없을 때").ToArray(),SelectedIndex=Array.IndexOf(minutes,main.AutomaticAwayMinutes)};
        AutomationProperties.SetName(away,"자동 자리비움 시간");stack.Children.Add(away);
        stack.Children.Add(new TextBlock{Text="키보드·마우스 입력 기준입니다. 입력하면 온라인으로 복귀하며 Windows를 잠그면 즉시 자리비움으로 표시합니다.",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,16)});
        away.SelectionChanged+=(_,_)=>{if(away.SelectedIndex<0)return;main.SetAutomaticAway(minutes[away.SelectedIndex]);Change();};
        void Change() { Appearance.Apply(Appearance.Palettes[choices.SelectedIndex].Id, glass.IsChecked == true); main.RefreshAppearance(); try { main.SavePreferences(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Windows.MessageBox.Show(this, "화면은 변경했지만 설정을 저장하지 못했습니다. 저장 공간과 폴더 권한을 확인하세요.", "화면 설정"); } }
        choices.SelectionChanged += (_, _) => Change(); glass.Checked += (_, _) => Change(); glass.Unchecked += (_, _) => Change();
        var done = new Button { Content = "완료", Style = (Style)FindResource("PrimaryButton") }; done.Click += (_, _) => Close(); stack.Children.Add(done);
    }
}
