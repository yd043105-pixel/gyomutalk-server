using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path=System.Windows.Shapes.Path;

namespace SchoolMessenger.Desktop;

// Small building blocks of the administrator design (AdminTheme.xaml): badges, avatars, icons and cards.
static class AdminUi
{
    public static TextBlock Label(string text,string brush="Muted",double size=12,bool strong=false)
    {
        var label=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal};
        label.SetResourceReference(TextBlock.ForegroundProperty,brush);return label;
    }
    public static TextBlock Heading(string text,double size=15){var heading=Label(text,"Ink",size);heading.FontWeight=FontWeights.Bold;return heading;}
    public static TextBlock Group(string text){var group=Label(text,"Faint",11,true);group.Margin=new Thickness(0,0,0,8);return group;}
    public static Border Card(UIElement child,double padding=20)
    {
        var card=new Border{Child=child,Padding=new Thickness(padding),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1)};
        card.SetResourceReference(Border.BackgroundProperty,"Surface");card.SetResourceReference(Border.BorderBrushProperty,"Line");return card;
    }
    static (string Back,string Fore) Tone(string tone)=>tone switch{"Success"=>("SuccessTint","Success"),"Warning"=>("WarningTint","Warning"),"Danger"=>("DangerTint","Danger"),"Accent"=>("Pale","Blue"),"News"=>("NewsTint","News"),_=>("Subtle","Muted")};
    public static Border Badge(string text,string tone,bool dot=true)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        if(dot)row.Children.Add(new Ellipse{Width=6,Height=6,Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center});
        row.Children.Add(new TextBlock{Text=text,FontSize=11,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var badge=new Border{Child=row,Padding=new Thickness(8,3,8,3),CornerRadius=new CornerRadius(11),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        SetBadge(badge,text,tone);return badge;
    }
    public static void SetBadge(Border badge,string text,string tone)
    {
        var (back,fore)=Tone(tone);var row=(StackPanel)badge.Child;badge.SetResourceReference(Border.BackgroundProperty,back);
        foreach(var child in row.Children){if(child is Ellipse dot)dot.SetResourceReference(Shape.FillProperty,fore);else if(child is TextBlock label){label.Text=text;label.SetResourceReference(TextBlock.ForegroundProperty,fore);}}
    }
    public static Border Avatar(string name,double size=32)
    {
        var initial=new TextBlock{Text=name.Length>0?name[..1]:"?",FontSize=size>40?16:13,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        initial.SetResourceReference(TextBlock.ForegroundProperty,"Blue");
        var avatar=new Border{Width=size,Height=size,CornerRadius=new CornerRadius(size/2),Child=initial,VerticalAlignment=VerticalAlignment.Center};
        avatar.SetResourceReference(Border.BackgroundProperty,"Pale");return avatar;
    }
    // Without a stroke brush the icon follows the enclosing control's foreground (Glyph style).
    public static Path Icon(string key,double size=16,string? stroke=null)
    {
        var icon=new Path{Width=size,Height=size,VerticalAlignment=VerticalAlignment.Center};
        icon.SetResourceReference(FrameworkElement.StyleProperty,"Glyph");icon.SetResourceReference(Path.DataProperty,key);
        if(stroke is not null)icon.SetResourceReference(Shape.StrokeProperty,stroke);return icon;
    }
    public static StackPanel IconLabel(string key,string text)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(Icon(key,14));
        row.Children.Add(new TextBlock{Text=text,Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center});return row;
    }
    public static Button CopyButton(System.Func<string> value,string name)
    {
        var copy=new Button{Content=Icon("AdminCopyIcon",15),Padding=new Thickness(6),MinHeight=0,Width=30,Height=30,ToolTip="복사",Margin=new Thickness(8,0,0,0)};
        System.Windows.Automation.AutomationProperties.SetName(copy,name+" 복사");
        copy.Click+=(_,_)=>{try{Clipboard.SetText(value());}catch(System.Runtime.InteropServices.ExternalException){}};return copy;
    }
}
