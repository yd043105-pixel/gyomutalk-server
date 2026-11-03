using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

// The notice in the corner of the screen: never takes the keyboard, stays while the pointer is on it, then fades out.
sealed class Toast:Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(nint window,int index,int value);
    const int ExStyle=-20,NoActivate=0x08000000,ToolWindow=0x80;
    public static readonly TimeSpan Stay=TimeSpan.FromSeconds(5);
    readonly TextBlock heading=AdminUi.Heading("",13),detail=AdminUi.Label("","Muted",12);
    readonly DispatcherTimer wait=new(){Interval=Stay};
    Action open=()=>{};
    public Toast()
    {
        Title="교무톡 알림";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=false;Topmost=true;ShowActivated=false;ResizeMode=ResizeMode.NoResize;SizeToContent=SizeToContent.WidthAndHeight;
        var bell=new System.Windows.Shapes.Path{Width=16,Height=16,StrokeThickness=1.3,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};bell.SetResourceReference(System.Windows.Shapes.Path.DataProperty,"BellOn");bell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Blue");
        var mark=new Border{Width=32,Height=32,CornerRadius=new CornerRadius(16),Child=bell,Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center};mark.SetResourceReference(Border.BackgroundProperty,"Pale");
        var words=new StackPanel{VerticalAlignment=VerticalAlignment.Center};detail.Margin=new Thickness(0,4,0,0);detail.TextWrapping=TextWrapping.NoWrap;words.Children.Add(heading);words.Children.Add(detail);
        var close=new Button{Content="×",ToolTip="닫기",BorderThickness=new Thickness(0),Background=Brushes.Transparent,Padding=new Thickness(0),MinHeight=24,Width=24,Height=24,Margin=new Thickness(12,0,0,0),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};close.SetResourceReference(Control.ForegroundProperty,"Muted");System.Windows.Automation.AutomationProperties.SetName(close,"알림 닫기");close.Click+=(_,_)=>Close();
        var row=new DockPanel{MinWidth=260};row.Children.Add(mark);DockPanel.SetDock(mark,Dock.Left);row.Children.Add(close);DockPanel.SetDock(close,Dock.Right);row.Children.Add(words);
        var card=new Border{Child=row,Padding=new Thickness(14,12,10,12),CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),Margin=new Thickness(16),Cursor=Cursors.Hand,Effect=new DropShadowEffect{BlurRadius=16,ShadowDepth=2,Opacity=.16}};card.SetResourceReference(Border.BackgroundProperty,"Surface");card.SetResourceReference(Border.BorderBrushProperty,"Line");
        card.MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource is DependencyObject source&&!close.IsAncestorOf(source)){Close();open();}};
        Content=card;
        wait.Tick+=(_,_)=>FadeOut();
        MouseEnter+=(_,_)=>Hold();MouseLeave+=(_,_)=>wait.Start();
        Closed+=(_,_)=>wait.Stop();
        SourceInitialized+=(_,_)=>{var handle=new WindowInteropHelper(this).Handle;SetWindowLong(handle,ExStyle,GetWindowLong(handle,ExStyle)|NoActivate|ToolWindow);};
    }
    public void Present(string title,string text,Action click)
    {
        heading.Text=title;detail.Text=text;open=click;
        if(!IsVisible)Show();
        UpdateLayout();var area=SystemParameters.WorkArea;Left=area.Right-ActualWidth;Top=area.Bottom-ActualHeight;
        Hold();if(!IsMouseOver)wait.Start();
    }
    void Hold(){wait.Stop();BeginAnimation(OpacityProperty,null);Opacity=1;}
    void FadeOut()
    {
        wait.Stop();
        var fade=new DoubleAnimation(0,TimeSpan.FromMilliseconds(600));fade.Completed+=(_,_)=>{if(!IsMouseOver)Close();};
        BeginAnimation(OpacityProperty,fade);
    }
}
