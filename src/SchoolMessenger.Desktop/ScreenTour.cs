using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SchoolMessenger.Desktop;

// Presses every button of the running program against a synthetic school, saves a picture of each screen it reaches and
// lists what a person would call broken: text that is cut off, a control squeezed out of its space, sideways scrolling.
// Hand-picked screenshots only ever covered the screens someone remembered to add; this finds the screens by itself.
sealed class ScreenTour(string folder)
{
    // Presses that end the session, start another program or would show the real desktop.
    static readonly Regex Skip=new("로그아웃|프로그램 종료|^닫기$|끝내기|업데이트|다시 시작|서버 중지|서버 멈춤|서버 시작|시간표 생성기|원격|메모장|관리 프로그램|백업|복구|옮기|설치|최대화|최소화|잠금|잠그|^인쇄",RegexOptions.CultureInvariant);
    // Pressed after everything else, because they change what the other screens look like or remove what they show.
    static readonly Regex Late=new("화면 색상|삭제|지우|초기화|회수|나가기|비활성|거절|취소",RegexOptions.CultureInvariant);
    // YY_SCREEN_TOUR_ONLY narrows the first window to the buttons whose name matches, to look at one part again.
    static readonly Regex? Only=Environment.GetEnvironmentVariable("YY_SCREEN_TOUR_ONLY") is{Length:>0} only?new Regex(only):null;
    static readonly Regex Agree=new("^(예|확인|닫기|나가기|버리기|저장 안 함)",RegexOptions.CultureInvariant);
    static readonly MethodInfo Click=typeof(ButtonBase).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!;
    const int MostPresses=700,MostAlike=14,MostPages=25,Deepest=3;
    delegate bool EachWindow(nint window,nint state);
    [DllImport("user32.dll")]static extern bool EnumThreadWindows(uint thread,EachWindow each,nint state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(nint window,StringBuilder name,int length);
    [DllImport("user32.dll")]static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]static extern bool PostMessage(nint window,uint message,nint w,nint l);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();

    readonly List<(string File,string Where,string Action,string Size,string[] Faults)> shots=new();
    readonly List<string> notes=new(),trail=new();readonly HashSet<string> pressed=new(),pictures=new();
    readonly Dictionary<string,int> alike=new();
    // When each control first appeared. What a press just brought up is pressed before what was there all along,
    // so a page is gone through before the menu moves on to the next one.
    readonly Dictionary<FrameworkElement,int> born=new();readonly HashSet<Type> sized=new();
    // A button that stays on screen from page to page (a menu on the left) is pressed once, not once for every page.
    readonly HashSet<FrameworkElement> touched=new();readonly Dictionary<string,HashSet<string>> pages=new();
    readonly DateTime stop=DateTime.UtcNow.AddMinutes(14);int presses,clock;

    public async Task Run(Window root)
    {
        Directory.CreateDirectory(folder);foreach(var old in Directory.GetFiles(folder,"*.png"))File.Delete(old);
        DispatcherUnhandledExceptionEventHandler failed=(_,e)=>{notes.Add("오류: "+e.Exception.GetBaseException().Message);e.Handled=true;};
        Application.Current.DispatcherUnhandledException+=failed;
        // Windows that were open before the first press (the administrator window opens at sign-in) are toured as well.
        try{var already=Open().Where(w=>w!=root).ToList();await Explore(root,0,"처음 화면");foreach(var other in already.Where(w=>w.IsVisible))await Explore(other,1,"처음부터 열려 있던 창");}
        catch(Exception error){notes.Add("순회 중단: "+error);}
        finally{Application.Current.DispatcherUnhandledException-=failed;Write();}
    }

    static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        var rest=new Stack<DependencyObject>();rest.Push(root);
        while(rest.Count>0){var node=rest.Pop();yield return node;for(var i=VisualTreeHelper.GetChildrenCount(node)-1;i>=0;i--)rest.Push(VisualTreeHelper.GetChild(node,i));}
    }
    static IEnumerable<DependencyObject> Ancestors(DependencyObject node){for(var up=VisualTreeHelper.GetParent(node);up is not null;up=VisualTreeHelper.GetParent(up))yield return up;}
    static string Short(string text,int length=40){text=Regex.Replace(text,@"\s+"," ").Trim();return text.Length>length?text[..length]:text;}
    static string Label(FrameworkElement e)
    {
        if(AutomationProperties.GetName(e) is{Length:>0} named)return Short(named);
        if(e is HeaderedItemsControl{Header:string header})return Short(header);
        if(e is ContentControl{Content:string content})return Short(content);
        var text=string.Join(" ",Tree(e).OfType<TextBlock>().Where(t=>t.IsVisible&&t.Text.Trim().Length>0).Select(t=>t.Text.Trim()).Take(3));
        return text.Length>0?Short(text):e.ToolTip as string is{Length:>0} tip?Short(tip):e.Name.Length>0?e.Name:e.GetType().Name;
    }
    static string Place(DependencyObject e)=>string.Join(">",Ancestors(e).Select(a=>a.GetType().Name).Reverse());
    static FrameworkElement Face(Window window)=>VisualTreeHelper.GetChildrenCount(window)>0?(FrameworkElement)VisualTreeHelper.GetChild(window,0):window;
    static HashSet<Window> Open()=>Application.Current.Windows.Cast<Window>().Where(w=>w.IsVisible).ToHashSet();
    static List<FrameworkElement> Popups()=>PresentationSource.CurrentSources.OfType<HwndSource>().Where(s=>!s.IsDisposed).Select(s=>s.RootVisual).OfType<FrameworkElement>().Where(r=>r is not Window&&r.IsVisible&&r.ActualWidth>1).ToList();
    static async Task Settle(){await Task.Delay(300);await Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);}
    // A page that loads from the server fills in a moment after the press; wait until the window stops changing.
    static async Task Settle(Window window)
    {
        await Settle();if(!window.IsVisible)return;var last=Look(Face(window));
        for(var i=0;i<8&&window.IsVisible;i++){await Task.Delay(250);var now=Look(Face(window));if(now==last)break;last=now;}
    }

    // The screen a window is showing: its type, its title and the page heading inside it, so "저장" on one page of the
    // administrator program is not taken for "저장" on another.
    string Page(Window window)
    {
        var kind=window.GetType().Name;var heading=Tree(window).OfType<TextBlock>().FirstOrDefault(t=>t.IsVisible&&t.FontSize>=20&&t.Text.Trim().Length>0)?.Text??"";
        var known=pages.TryGetValue(kind,out var set)?set:pages[kind]=new();if(!known.Contains(heading)&&known.Count>=MostPages)heading="";else known.Add(heading);
        return kind+":"+Short(window.Title,30)+":"+Short(heading,30);
    }
    static IEnumerable<(FrameworkElement Element,string Kind)> Pressables(Window window)
    {
        var lists=new HashSet<DependencyObject>();var menus=new HashSet<string>();
        foreach(var e in Tree(window).OfType<FrameworkElement>())
        {
            if(!e.IsVisible||!e.IsEnabled||!e.IsHitTestVisible)continue;
            switch(e)
            {
                case RepeatButton:break;
                case ButtonBase b:
                    if(b.TemplatedParent is not(ComboBox or ScrollBar or TextBoxBase or PasswordBox or DatePicker or Slider or DataGridColumnHeader))yield return (b,b is ToggleButton and not RadioButton?"toggle":"button");
                    break;
                case ComboBox c:yield return (c,"combo");break;
                case TabItem{IsSelected:false} t:yield return (t,"tab");break;
                case ListBoxItem or TreeViewItem or DataGridRow:
                    if(ItemsControl.ItemsControlFromItemContainer(e) is{} owner&&lists.Add(owner))yield return (e,"item");
                    break;
                default:
                    if(e.Cursor==Cursors.Hand&&!Ancestors(e).Any(a=>a is ButtonBase))yield return (e,"hand");
                    break;
            }
            if(e.ContextMenu is not null&&e is not ButtonBase&&menus.Add(Place(e)))yield return (e,"menu");
        }
    }
    static void Press(FrameworkElement e,string kind)=>e.Dispatcher.BeginInvoke(()=>
    {
        MouseButtonEventArgs Mouse(RoutedEvent routed)=>new(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=routed};
        switch(kind)
        {
            case "button":case "toggle":Click.Invoke(e,null);break;
            case "combo":((ComboBox)e).IsDropDownOpen=true;break;
            case "tab":((TabItem)e).IsSelected=true;break;
            case "item":
                if(e is ListBoxItem row)row.IsSelected=true;else if(e is TreeViewItem node){node.IsSelected=true;node.IsExpanded=true;}else if(e is DataGridRow line)line.IsSelected=true;
                e.RaiseEvent(Mouse(Control.MouseDoubleClickEvent));(ItemsControl.ItemsControlFromItemContainer(e) as Control)?.RaiseEvent(Mouse(Control.MouseDoubleClickEvent));break;
            case "hand":e.RaiseEvent(Mouse(System.Windows.Input.Mouse.MouseDownEvent));e.RaiseEvent(Mouse(System.Windows.Input.Mouse.MouseUpEvent));break;
            case "menu":e.ContextMenu!.PlacementTarget=e;e.ContextMenu.IsOpen=true;break;
        }
    });
    static void ClosePopups()
    {
        foreach(var popup in Popups())
        {
            foreach(var menu in Tree(popup).OfType<ContextMenu>())menu.IsOpen=false;
            foreach(var tip in Tree(popup).OfType<ToolTip>())tip.IsOpen=false;
            if(popup.Parent is Popup owner)owner.IsOpen=false;
        }
    }
    // File pickers and other Windows dialogs are not ours to draw; they are closed so the tour can go on.
    static int CloseNative()
    {
        var closed=0;
        EnumThreadWindows(GetCurrentThreadId(),(window,_)=>
        {
            var name=new StringBuilder(64);GetClassName(window,name,64);
            if(name.ToString()=="#32770"&&IsWindowVisible(window)){PostMessage(window,0x0010,0,0);closed++;}
            return true;
        },0);
        return closed;
    }

    async Task Explore(Window window,int depth,string opened)
    {
        await Settle(window);Capture(Face(window),Page(window),opened);
        while(presses<MostPresses&&DateTime.UtcNow<stop&&window.IsVisible)
        {
            var page=Page(window);(FrameworkElement Element,string Kind,string Label,string Key)? next=null;
            foreach(var (element,kind) in Pressables(window))
            {
                var label=(kind=="menu"?"오른쪽 메뉴 · ":"")+Label(element);var key=page+"|"+kind+"|"+label;
                if(pressed.Contains(key)||touched.Contains(element)||Skip.IsMatch(label)||depth==0&&Only?.IsMatch(label)==false)continue;
                born.TryAdd(element,clock);
                if(next is null||(Late.IsMatch(label)?0:1,born[element]).CompareTo((Late.IsMatch(next.Value.Label)?0:1,born[next.Value.Element]))>0)next=(element,kind,label,key);
            }
            if(next is not{} press)break;
            pressed.Add(press.Key);touched.Add(press.Element);clock++;
            // Rows of one list look alike: a few of them show the screen, the rest only cost time.
            var group=window.GetType().Name+"|"+Place(press.Element)+"|"+press.Kind;alike[group]=alike.GetValueOrDefault(group)+1;
            if(alike[group]>(press.Element is CheckBox or RadioButton||press.Kind=="hand"?3:MostAlike))continue;
            presses++;var before=Open();var look=Look(Face(window));trail.Add(page+"	"+press.Kind+"	"+press.Label);
            Press(press.Element,press.Kind);await Settle(window);
            if(CloseNative()>0){notes.Add("Windows 창이 열림(파일 고르기 등): "+page+" → "+press.Label);await Settle();}
            foreach(var popup in Popups())
            {
                Capture(popup,page,press.Label+" · 펼친 목록");
                // The menu's own items are kept and pressed after it closes: a menu opened by code does not always open twice.
                var items=Tree(popup).OfType<MenuItem>().Where(i=>i.IsVisible&&i.IsEnabled&&!i.HasItems).Select(i=>(Item:i,Label:Label(i))).Where(i=>!Skip.IsMatch(i.Label)&&pressed.Add(page+"|menuitem|"+i.Label)).OrderBy(i=>Late.IsMatch(i.Label)).ToList();
                ClosePopups();await Settle();
                foreach(var (item,label) in items)
                {
                    if(presses>=MostPresses||!window.IsVisible)break;
                    var earlier=Open();var was=Look(Face(window));presses++;trail.Add(page+"	menuitem	"+press.Label+" → "+label);
                    _=item.Dispatcher.BeginInvoke(()=>item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                    await Settle();await Follow(window,earlier,was,press.Label+" → "+label,depth);
                }
                break;
            }
            ClosePopups();
            await Follow(window,before,look,press.Label,depth);
            // A switch is put back, so that turning a feature off does not hide the screens still to be visited.
            if(press.Kind=="toggle"&&window.IsVisible&&press.Element.IsVisible){var again=Open();Press(press.Element,press.Kind);await Settle();await Follow(window,again,Look(Face(window)),press.Label+" 되돌림",depth);}
        }
        if(window.IsVisible&&window is not{Owner:not null,ShowInTaskbar:false}&&window.GetType()!=typeof(Window)&&sized.Add(window.GetType())&&window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip&&window.WindowState==WindowState.Normal)
        {
            // People do shrink these windows: at the smallest size allowed, nothing may be cut off.
            var (width,height)=(window.Width,window.Height);
            window.Width=window.MinWidth>0?window.MinWidth:Math.Max(360,width*0.6);window.Height=window.MinHeight>0?window.MinHeight:Math.Max(320,height*0.6);
            await Settle();Capture(Face(window),Page(window),"가장 작게 줄인 창");window.Width=width;window.Height=height;
            window.WindowState=WindowState.Maximized;await Settle();Capture(Face(window),Page(window),"최대화한 창");window.WindowState=WindowState.Normal;await Settle();
        }
    }
    // What a press led to: windows that opened are toured and closed, and this window is pictured again if it changed.
    async Task Follow(Window window,HashSet<Window> before,string look,string action,int depth)
    {
        for(var round=0;round<4;round++)
        {
            if(CloseNative()>0)await Settle();
            var opened=Open().Where(w=>!before.Contains(w)).ToList();if(opened.Count==0)break;
            trail.Add("	→ 창 열림	"+string.Join(", ",opened.Select(w=>w.GetType().Name+" “"+w.Title+"”")));
            foreach(var other in opened)
            {
                if(depth<Deepest)await Explore(other,depth+1,action);else Capture(Face(other),Page(other),action);
                await Close(other);if(other.IsVisible){before.Add(other);notes.Add("닫히지 않은 창: "+other.Title);}
            }
        }
        if(window.IsVisible&&Look(Face(window))!=look)Capture(Face(window),Page(window),action);
    }
    async Task Close(Window window)
    {
        if(!window.IsVisible)return;var before=Open();
        _=window.Dispatcher.BeginInvoke(()=>{try{window.Close();}catch(InvalidOperationException){}});await Settle();
        foreach(var ask in Open().Where(w=>!before.Contains(w)).ToList())
        {
            Capture(Face(ask),Page(ask),"닫을 때 묻는 창 · "+window.Title);
            if(Tree(ask).OfType<Button>().FirstOrDefault(b=>b.IsVisible&&Agree.IsMatch(Label(b))) is{} yes)Press(yes,"button");else _=ask.Dispatcher.BeginInvoke(()=>ask.Close());
            await Settle();
        }
    }

    static RenderTargetBitmap? Picture(FrameworkElement visual)
    {
        visual.UpdateLayout();if(visual.ActualWidth<2||visual.ActualHeight<2)return null;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);return bitmap;
    }
    static string Look(FrameworkElement visual)
    {
        if(Picture(visual) is not{} bitmap)return "";
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return Convert.ToHexString(SHA1.HashData(pixels));
    }
    void Capture(FrameworkElement visual,string where,string action)
    {
        if(Picture(visual) is not{} bitmap)return;
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);if(!pictures.Add(Convert.ToHexString(SHA1.HashData(pixels))))return;
        // A popup has no background of its own in the picture; windows already paint theirs.
        var name=$"{shots.Count+1:000} {Regex.Replace(Short(where.Split(':')[0]+" "+action,60),@"[^\w가-힣 ·\-]","")}.png";
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(folder,name)))encoder.Save(file);
        shots.Add((name,where,action,bitmap.PixelWidth+"×"+bitmap.PixelHeight,Faults(visual).Distinct().Take(14).ToArray()));
        if(shots.Count%20==0)Write();
    }
    // WPF clips an element that was given less room than it asked for; that clip is exactly "잘렸다".
    static IEnumerable<string> Faults(FrameworkElement root)
    {
        foreach(var e in Tree(root).OfType<FrameworkElement>())
        {
            if(!e.IsVisible||e.ActualWidth<1)continue;
            var squeezed=!e.ClipToBounds&&LayoutInformation.GetLayoutClip(e) is not null;
            if(e is TextBlock text&&text.Text.Trim().Length>0)
            {
                if(squeezed&&text.TextTrimming==TextTrimming.None)yield return "글씨 잘림: “"+Short(text.Text,24)+"”";
                if(text.FontSize<10.5)yield return "글씨 작음("+text.FontSize+"): “"+Short(text.Text,16)+"”";
            }
            else if(squeezed&&e is ButtonBase or TextBoxBase or PasswordBox or ComboBox)yield return "자리 부족: "+e.GetType().Name+" “"+Label(e)+"”";
            if(e is ScrollViewer scroll&&scroll.TemplatedParent is not(TextBoxBase or PasswordBox))
            {
                if(scroll.ComputedHorizontalScrollBarVisibility==Visibility.Visible&&scroll.ScrollableWidth>0)yield return "가로 스크롤 생김";
                else if(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible&&scroll.ScrollableHeight>0)yield return "세로 스크롤";
            }
        }
    }
    void Write()
    {
        var text=new StringBuilder();text.AppendLine($"화면 {shots.Count}개 · 누른 횟수 {presses}");
        foreach(var note in notes.Distinct())text.AppendLine("! "+note);
        foreach(var shot in shots)text.AppendLine($"{shot.File}\t{shot.Where}\t{shot.Action}\t{shot.Size}\t{string.Join(" / ",shot.Faults)}");
        File.WriteAllText(Path.Combine(folder,"report.txt"),text.ToString());File.WriteAllLines(Path.Combine(folder,"presses.txt"),trail);
        string Html(string value)=>System.Net.WebUtility.HtmlEncode(value);
        File.WriteAllText(Path.Combine(folder,"index.html"),"<!doctype html><meta charset=\"utf-8\"><title>화면 순회</title><style>body{font:14px 'Malgun Gothic',sans-serif;background:#f4f7fb;margin:24px}figure{margin:0 0 28px;background:#fff;border:1px solid #dde3ec;border-radius:10px;padding:14px}img{max-width:100%;border:1px solid #e6eaf0}figcaption{margin-bottom:10px}b{color:#c0392b}</style>"
            +string.Concat(shots.Select(s=>$"<figure><figcaption>{Html(s.File)} · {Html(s.Action)} · {s.Size}{(s.Faults.Length>0?"<br><b>"+Html(string.Join(" / ",s.Faults))+"</b>":"")}</figcaption><img loading=\"lazy\" src=\"{Uri.EscapeDataString(s.File)}\"></figure>")));
    }
}
