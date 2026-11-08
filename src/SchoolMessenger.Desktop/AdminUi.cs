using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Path=System.Windows.Shapes.Path;

namespace SchoolMessenger.Desktop;

// Small building blocks of the administrator design (AdminTheme.xaml): badges, avatars, icons and cards.
static class AdminUi
{
    public static TextBlock Label(string text,string brush="Muted",double size=13,bool strong=false)
    {
        var label=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal};
        label.SetResourceReference(TextBlock.ForegroundProperty,brush);return label;
    }
    public static TextBlock Heading(string text,double size=15){var heading=Label(text,"Ink",size);heading.FontWeight=FontWeights.Bold;return heading;}
    public static StackPanel PageTitle(string title, string description)
    {
        var block = new StackPanel();
        block.Children.Add(Heading(title, 22));
        var subtitle = Label(description, "Muted", 13);
        subtitle.Margin = new Thickness(0, 4, 0, 0);
        block.Children.Add(subtitle);
        return block;
    }
    public static TextBlock Group(string text){var group=Label(text,"Faint",12,true);group.Margin=new Thickness(0,0,0,8);return group;}
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
        row.Children.Add(new TextBlock{Text=text,FontSize=12,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var badge=new Border{Child=row,Padding=new Thickness(8,3,8,3),CornerRadius=new CornerRadius(11),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};
        SetBadge(badge,text,tone);return badge;
    }
    public static void SetBadge(Border badge,string text,string tone)
    {
        var (back,fore)=Tone(tone);var row=(StackPanel)badge.Child;badge.SetResourceReference(Border.BackgroundProperty,back);
        foreach(var child in row.Children){if(child is Ellipse dot)dot.SetResourceReference(Shape.FillProperty,fore);else if(child is TextBlock label){label.Text=text;label.SetResourceReference(TextBlock.ForegroundProperty,fore);}}
    }
    public static Border Avatar(string name,double size=32,string? seed=null)=>SchoolMessenger.Desktop.Avatar.Create(name,size,seed);
    // Without a stroke brush the icon follows the enclosing control's foreground (Glyph style).
    public static Path Icon(string key,double size=16,string? stroke=null)
    {
        var icon=new Path{Width=size,Height=size,VerticalAlignment=VerticalAlignment.Center};
        icon.SetResourceReference(FrameworkElement.StyleProperty,"Glyph");icon.SetResourceReference(Path.DataProperty,key);
        if(stroke is not null)icon.SetResourceReference(Shape.StrokeProperty,stroke);return icon;
    }
    // A window built in code: the school title bar with a plain page under it.
    public static void Page(Window window,UIElement content)
    {
        var page=new Border{Child=content};page.SetResourceReference(Border.BackgroundProperty,"Surface");
        window.Content=page;window.Style=(Style)window.FindResource("SchoolWindow");
    }
    // Shown instead of an empty list or table, so a blank screen always says what belongs there.
    public static StackPanel Empty(string title,string detail,Button? action=null)
    {
        var panel=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(24,48,24,48)};
        panel.Children.Add(EmptyArt(title.Contains("대화")?"ChatIcon":title.Contains("설문")?"SurveyIcon":title.Contains("시간표")||title.Contains("수업")||title.Contains("교체")?"TimetableIcon":title.Contains("메시지")?"MailIcon":title.Contains("할 일")||title.Contains("제출")?"FileIcon":title.Contains("교직원")?"TreeIcon":title.Contains("특별실")||title.Contains("일정")?"CalendarIcon":"FolderIcon"));
        var heading=Heading(title);heading.TextAlignment=TextAlignment.Center;panel.Children.Add(heading);
        var text=Label(detail);text.TextAlignment=TextAlignment.Center;text.Margin=new Thickness(0,8,0,0);panel.Children.Add(text);
        if(action is not null){action.HorizontalAlignment=HorizontalAlignment.Center;action.Margin=new Thickness(0,16,0,0);panel.Children.Add(action);}
        return panel;
    }
    public static Border EmptyCard(string title, string detail, Button? action = null)
    {
        var content = Empty(title, detail, action);
        content.Margin = new Thickness(16, 24, 16, 24);
        var card = Card(content, 0);
        card.SetResourceReference(Border.BackgroundProperty, "Subtle");
        return card;
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
    // Two stacked cards with the section's icon: the picture above every empty list.
    public static Grid EmptyArt(string icon)
    {
        var art=new Grid{Width=92,Height=70,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,0,0,18),IsHitTestVisible=false};
        var back=new Border{Width=62,Height=48,CornerRadius=new CornerRadius(14),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        back.SetResourceReference(Border.BackgroundProperty,"Pale");art.Children.Add(back);
        var front=new Border{Width=62,Height=48,CornerRadius=new CornerRadius(14),BorderThickness=new Thickness(1.5),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Child=Icon(icon,24,"Blue")};
        front.SetResourceReference(Border.BackgroundProperty,"Surface");front.SetResourceReference(Border.BorderBrushProperty,"Blue");art.Children.Add(front);
        return art;
    }
    // Form buttons built from a label: finishing actions are primary, going back is a text link, undoing work is red.
    public static Button Tone(Button button,string label)
    {
        var key=label.EndsWith("으로")||label.EndsWith("돌아가기")?"SubtleButton":System.Text.RegularExpressions.Regex.IsMatch(label,"취소|삭제|나가기|종료")?"DangerButton":label.EndsWith("저장")||label.EndsWith("제출")||label.EndsWith("접수")?"PrimaryButton":null;
        if(key is not null)button.SetResourceReference(FrameworkElement.StyleProperty,key);
        if(key=="SubtleButton")button.Content="‹  "+label;
        return button;
    }
    // A person to tick in a picker: avatar, name and department; the accessible name and search text is the label.
    public static CheckBox PersonCheck(Person person,bool selected)
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal};var face=SchoolMessenger.Desktop.Avatar.Create(person.Name,28);face.Margin=new Thickness(0,0,10,0);row.Children.Add(face);
        var words=new StackPanel{VerticalAlignment=VerticalAlignment.Center};words.Children.Add(new TextBlock{Text=person.Name,FontSize=13,FontWeight=FontWeights.SemiBold});
        var role=string.Join(" · ",new[]{person.Position,person.Department}.Where(t=>t.Length>0).Distinct());if(role.Length>0)words.Children.Add(Label(role,"Faint",11));row.Children.Add(words);
        var check=new CheckBox{Content=row,IsChecked=selected,Margin=new Thickness(2,5,2,5),ToolTip=person.Label};System.Windows.Automation.AutomationProperties.SetName(check,person.Label);return check;
    }
    // A search box with the magnifier and a hint that disappears once something is typed.
    public static Grid SearchField(TextBox box,string hint)
    {
        box.Padding=new Thickness(32,8,8,8);box.SetResourceReference(Control.BackgroundProperty,"Soft");box.SetResourceReference(Control.BorderBrushProperty,"Soft");
        System.Windows.Automation.AutomationProperties.SetName(box,hint);
        var host=new Grid{Margin=box.Margin};box.Margin=new Thickness(0);host.Children.Add(box);
        var icon=Icon("SearchIcon",16,"Faint");icon.HorizontalAlignment=HorizontalAlignment.Left;icon.Margin=new Thickness(11,0,0,0);icon.IsHitTestVisible=false;host.Children.Add(icon);
        var label=Label(hint,"Faint",12);label.Margin=new Thickness(34,0,0,0);label.VerticalAlignment=VerticalAlignment.Center;label.IsHitTestVisible=false;host.Children.Add(label);
        box.TextChanged+=(_,_)=>label.Visibility=box.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
        return host;
    }
}
