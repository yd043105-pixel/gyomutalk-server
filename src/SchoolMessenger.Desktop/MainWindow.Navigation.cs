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
        var headings=NavigationItems.Children.OfType<Border>().Sum(h=>h.DesiredSize.Height);
        var compact=ActualWidth<420||NavigationLayout.ActualHeight<buttons.Length*42+headings;
        foreach(var button in buttons)
        {
            button.MinHeight=compact?22:38;button.Padding=new Thickness(3,compact?2:1,3,compact?2:1);
            if(button.Content is not StackPanel content)continue;
            foreach(var icon in content.Children.OfType<Path>())icon.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
            foreach(var label in content.Children.OfType<TextBlock>())label.Margin=compact?new Thickness(0):new Thickness(0,3,0,0);
        }
    }
}
