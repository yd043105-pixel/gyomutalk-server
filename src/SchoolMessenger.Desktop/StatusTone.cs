using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// Status lines carry both "저장했습니다" and "저장하지 못했습니다". Only problems are coloured, so a success never reads as an error.
static class StatusTone
{
    // ponytail: wording heuristic shared by every status line; give a line its own brush if a message is misread.
    static readonly Regex Problem=new("못했|못합니다|실패|오류|만료|거부|수 없|입력하세요|선택하세요|잘못|초과|권한이 없|찾지 못",RegexOptions.CultureInvariant);
    public static bool IsProblem(string text)=>Problem.IsMatch(text);
    public static void Watch(params TextBlock[] labels)
    {
        foreach(var label in labels)
        {
            // An empty line takes no room, so windows do not end in a blank strip.
            void Paint(){label.SetResourceReference(TextBlock.ForegroundProperty,IsProblem(label.Text)?"Danger":"Muted");label.Visibility=label.Text.Length==0?Visibility.Collapsed:Visibility.Visible;}
            void Changed(object? sender,System.EventArgs e)=>Paint();
            // The descriptor holds its handlers strongly; release them with the window so closed windows can be collected.
            var text=DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty,typeof(TextBlock));
            label.Loaded+=(_,_)=>{text.RemoveValueChanged(label,Changed);text.AddValueChanged(label,Changed);Paint();};
            label.Unloaded+=(_,_)=>text.RemoveValueChanged(label,Changed);
        }
    }
}
