using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Path=System.Windows.Shapes.Path;

namespace SchoolMessenger.Desktop;

public partial class MainWindow
{
    void InitializeNavigation()
    {
        SizeChanged+=(_,_)=>UpdateNavigationLayout();
        NavigationLayout.SizeChanged+=(_,_)=>UpdateNavigationLayout();
        NavigationItems.SizeChanged+=(_,_)=>UpdateNavigationLayout();
    }
    void UpdateNavigationLayout()
    {
        if(NavigationLayout is null||NavigationItems is null)return;
        var buttons=NavigationItems.Children.OfType<Button>().Append(SettingsButton).Where(b=>b.Visibility==Visibility.Visible).ToArray();
        // Labels go first when the window is short; the icons and tooltips stay.
        var compact=NavigationLayout.ActualHeight<buttons.Length*54;
        foreach(var button in buttons)
        {
            button.MinHeight=compact?38:50;button.Padding=new Thickness(0,compact?4:6,0,compact?4:6);
            var content=button.Content as StackPanel??(button.Content as Grid)?.Children.OfType<StackPanel>().FirstOrDefault();
            if(content is null)continue;
            foreach(var label in content.Children.OfType<TextBlock>())label.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        }
    }
}
