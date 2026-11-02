using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SchoolMessenger.Shared;

namespace SchoolMessenger.Desktop;

// Search-as-you-type over the national school list. The box keeps the school name; Selected is the exact school, since
// names repeat across regions and only the address tells them apart.
internal sealed class SchoolPicker
{
    public TextBox Box{get;}
    public TextBlock Choice{get;}=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};
    public SchoolEntry? Selected{get;private set;}
    public event Action? Changed;
    readonly ListBox list=new(){MaxHeight=280,BorderThickness=new Thickness(0)};
    readonly Popup popup;
    bool setting;
    public SchoolPicker(TextBox box)
    {
        Box=box;Choice.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        var frame=new Border{BorderThickness=new Thickness(1),Child=list};frame.SetResourceReference(Border.BackgroundProperty,"Surface");frame.SetResourceReference(Border.BorderBrushProperty,"Line");
        popup=new Popup{PlacementTarget=box,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,Child=frame};
        box.TextChanged+=(_,_)=>{if(setting)return;Selected=null;Show();Changed?.Invoke();Search();};
        box.PreviewKeyDown+=(_,e)=>
        {
            if(!popup.IsOpen)return;
            if(e.Key==Key.Down){list.SelectedIndex=Math.Min(list.SelectedIndex+1,list.Items.Count-1);list.ScrollIntoView(list.SelectedItem);e.Handled=true;}
            else if(e.Key==Key.Up){list.SelectedIndex=Math.Max(list.SelectedIndex-1,0);list.ScrollIntoView(list.SelectedItem);e.Handled=true;}
            else if(e.Key==Key.Enter&&list.SelectedItem is ListBoxItem{Tag:SchoolEntry school}){Pick(school);e.Handled=true;}
            else if(e.Key==Key.Escape){popup.IsOpen=false;e.Handled=true;}
        };
        list.PreviewMouseLeftButtonUp+=(_,e)=>{if((e.OriginalSource as DependencyObject) is { } source&&ItemsControl.ContainerFromElement(list,source) is ListBoxItem{Tag:SchoolEntry school})Pick(school);};
        Show();
    }
    void Search()
    {
        var found=SchoolList.Search(Box.Text);
        list.ItemsSource=found.Select(s=>
        {
            var lines=new StackPanel{Margin=new Thickness(2,3,2,3)};lines.Children.Add(new TextBlock{Text=s.Name,FontWeight=FontWeights.SemiBold});
            var address=new TextBlock{Text=s.Address.Length>0?s.Address:"주소 없음",FontSize=12};address.SetResourceReference(TextBlock.ForegroundProperty,"Muted");lines.Children.Add(address);
            return new ListBoxItem{Content=lines,Tag=s};
        }).ToArray();
        list.SelectedIndex=found.Length>0?0:-1;popup.MinWidth=Box.ActualWidth;popup.IsOpen=found.Length>0&&Box.IsKeyboardFocusWithin;
    }
    void Pick(SchoolEntry school){Set(school.Name,school.Address);popup.IsOpen=false;Box.CaretIndex=Box.Text.Length;}
    // Restores a saved choice; a name that is not in the list (or matches several schools) stays unconfirmed.
    public void Set(string name,string address)
    {
        setting=true;Box.Text=name;setting=false;
        Selected=SchoolList.All.FirstOrDefault(s=>SchoolList.Same(s,name,address));Show();Changed?.Invoke();
    }
    void Show()
    {
        var count=Selected is null?SchoolList.Named(Box.Text).Length:0;
        Choice.Text=Selected is {} s?"선택한 학교: "+s.Name+" · "+(s.Address.Length>0?s.Address:"주소 없음")
            :count>1?"같은 이름의 학교가 "+count+"곳 있습니다. 목록에서 주소를 보고 고르세요."
            :Box.Text.Trim().Length==0?"학교 이름이나 지역으로 검색해 목록에서 고르세요. 예: 중앙고 서울":"목록에서 학교를 고르면 주소로 같은 이름의 학교를 구분합니다.";
    }
}
