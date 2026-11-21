// The 과목 lines of the 수강신청 editor: typing a 2022 개정 course name fills its 교과(군)·유형, pasting a list makes one line each,
// Enter adds a line, and the teacher's own choice survives. Runs the real control in an off-screen window.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SchoolMessenger.Desktop;

var thread = new Thread(Run); thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
return failed ? 1 : 0;

partial class Program
{
    static bool failed;
    static void Check(bool ok, string text) { Console.WriteLine((ok ? "PASS " : "FAIL ") + text); failed |= !ok; }

    static void Run()
    {
        try
        {
            Check(CourseCatalog.All.Length > 150 && CourseCatalog.All.All(c => CourseCatalog.Areas.Contains(c.Area) && CourseCatalog.Types.Contains(c.Type)) && CourseCatalog.All.DistinctBy(c => c.Name).Count() == CourseCatalog.All.Length,
                "every listed course has one of the 10 교과(군) and 4 유형, and no name twice");
            Check(CourseCatalog.Find("미적분 1")?.Name == "미적분Ⅰ" && CourseCatalog.Find("기술가정")?.Area == "기술가정/정보" && CourseCatalog.Find("영어II")?.Name == "영어Ⅱ" && CourseCatalog.Find("물리학Ⅱ") is null,
                "a name is found despite spaces, 1/I/Ⅰ and the middle dot; an old-curriculum name is not");
            string[] Names(string typed) => CourseCatalog.Search(typed).Select(c => c.Name).ToArray();
            Check(Names("물리ㅎ").Contains("물리학") && Names("물리하").Contains("물리학") && Names("물맇").Contains("물리학") && !Names("물릴").Contains("물리학") && Names("고").Contains("과학탐구실험1") && Names("물리").Contains("물리학") && Names("ㅁ").First() == "문학" && Names("과학").First() == "과학탐구실험1" && Names("탐구").Contains("수학과제 탐구") && Names("뷁").Length == 0 && Names("").Length == CourseCatalog.All.Length,
                "the list narrows to names holding what was typed, names starting with it first, and a Korean letter still being typed (ㅎ, 하, 맇 before 리학, 고 before 과) already matches");

            var rows = new CourseRows();
            var window = new Window { Content = rows, Width = 640, Height = 400, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            window.Show();
            var first = rows.Add(""); var mine = rows.Add("우리학교 특강", ["교양", "융합선택"]); Settle();
            window.Activate(); first.Focus(); Settle();
            Check(!rows.ListOpen, "the list does not open on its own when the box is entered");
            Type(first, "미적");
            Check(rows.ListOpen && rows.Listed.Select(c => c.Name).SequenceEqual(["미적분Ⅰ", "미적분Ⅱ"]) && first.Text == "미적",
                "typing opens a list narrowed to what was typed, and the box keeps exactly what was typed");
            Type(first, "미적분");
            Check(Kind(first) == "|", "a half-typed name picks nothing yet");
            var item = (ListBoxItem)Lists(rows).ItemContainerGenerator.ContainerFromIndex(1);
            item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent }); Settle();
            Check(first.Text == "미적분Ⅱ" && Kind(first) == "수학|진로선택" && !rows.ListOpen && first.IsKeyboardFocused, "clicking a course in the list puts it in the box with its kind, closes the list and keeps typing there");
            Type(first, "미적분 1");
            Check(Kind(first) == "수학|일반선택", "typing a listed name picks its 교과(군) and 유형");
            Check(rows.Items[0].Name == "미적분Ⅰ", "a listed course is saved under its official name however it was typed");
            mine.Focus(); Settle();
            Check(first.Text == "미적분Ⅰ", "leaving the box shows the official name");
            Type(first, "미적분 심화(학교 지정)");
            Check(Kind(first) == "|", "changing the name to one not on the list clears what was filled in for it");
            Type(mine, "우리학교 특강 2");
            Check(Kind(mine) == "교양|융합선택", "a kind the teacher chose for the school's own course is kept while its name changes");

            var box = first;
            var paste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "물리학\r\n화학\r\n\r\n역학과 에너지\r\n"), false, DataFormats.UnicodeText);
            box.RaiseEvent(paste); Settle();
            var names = rows.Items.Select(i => i.Name).ToArray();
            Check(paste.CommandCancelled && names.SequenceEqual(["물리학", "화학", "역학과 에너지", "우리학교 특강 2"]) && rows.Items[2].Kind.SequenceEqual(["과학", "진로선택"]),
                "pasting a list fills this line and adds one line per course right after it, each with its kind");

            var before = rows.Children.Count;
            var source = PresentationSource.FromVisual(box)!;
            box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Settle();
            Check(rows.Children.Count == before + 1 && rows.Items.Length == 4, "Enter adds an empty line under the current one; empty lines are not saved");

            var reopened = new CourseRows(); window.Content = reopened; reopened.Add("물리학", ["과학", "진로선택"]); Settle();
            Check(reopened.Items[0].Kind.SequenceEqual(["과학", "진로선택"]), "a saved kind is shown as saved, even for a listed course");
            window.Close();
        }
        catch (Exception error) { Check(false, error.ToString()); }
    }

    static void Settle() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    static void Type(TextBox course, string text) { course.Text = text; Settle(); }
    static string Kind(TextBox course) { var line = (Grid)((Grid)course.Parent).Parent; var (_, area, type) = ((TextBox, ComboBox, ComboBox))line.Tag; return $"{area.SelectedItem}|{type.SelectedItem}"; }
    static ListBox Lists(CourseRows rows) => (ListBox)typeof(CourseRows).GetField("list", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rows)!;
}
