using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace SchoolMessenger.Desktop;

// 결과 → 분반 편성: the curriculum teacher sets the conditions, the program splits each course into sections,
// a section's students are listed on click, the list goes to Excel, and the AI may explain the numbers.
public sealed partial class CourseWindow
{
    int sectionMost = 25, sectionFewest = 10; bool sectionKeep = true; string? sectionPicked;
    readonly Dictionary<string, int> sectionCounts = [];
    string? sectionExplained;

    void SectionsView(Results results, StackPanel box)
    {
        var round = results.Round;
        var courses = Sections.Courses(round.Groups.Select(g => new Sections.Choice(g.Name, g.Semester, g.Courses, g.Closed)),
            results.Students.Select(s => new Sections.Answer(new Sections.Pupil(s.Id, s.ClassNo, s.Number, s.Name), s.Picks)).ToArray());
        Sections.Options Options() => new(sectionMost, sectionFewest, sectionKeep, sectionCounts);
        void Redraw() { box.Children.Clear(); SectionsView(results, box); }

        box.Children.Add(Titled("분반 편성", null));
        var about = AdminUi.Label("제출한 학생으로 과목마다 분반을 나눕니다. 조건을 바꾸면 바로 다시 나누고, 과목마다 분반 수를 직접 바꿀 수 있습니다. 시간표의 같은 시간에 배치하는 일은 시간표 프로그램에서 합니다.", "Muted", 12);
        about.TextWrapping = TextWrapping.Wrap; about.Margin = new Thickness(0, 2, 0, 12); box.Children.Add(about);

        var conditions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        UIElement Field(string label, ComboBox choice) { var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 8) }; var text = AdminUi.Label(label, "Ink", 13); text.VerticalAlignment = VerticalAlignment.Center; text.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(text); row.Children.Add(choice); AutomationProperties.SetName(choice, label); return row; }
        var mosts = Enumerable.Range(10, 31).ToArray();
        var most = new ComboBox { ItemsSource = mosts.Select(n => n + "명").ToArray(), SelectedIndex = Array.IndexOf(mosts, sectionMost), Width = 90 };
        most.SelectionChanged += (_, _) => { if (most.SelectedIndex < 0) return; sectionMost = mosts[most.SelectedIndex]; sectionCounts.Clear(); sectionExplained = null; Redraw(); };
        conditions.Children.Add(Field("한 분반 최대", most));
        var fewests = new[] { 0, 5, 8, 10, 12, 13, 15, 20 };
        var fewest = new ComboBox { ItemsSource = fewests.Select(n => n == 0 ? "표시 안 함" : n + "명 미만").ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(fewests, sectionFewest)), Width = 120 };
        fewest.SelectionChanged += (_, _) => { if (fewest.SelectedIndex < 0) return; sectionFewest = fewests[fewest.SelectedIndex]; sectionExplained = null; Redraw(); };
        conditions.Children.Add(Field("인원이 적은 과목 표시", fewest));
        var keep = new CheckBox { Content = "같은 반 학생은 되도록 같은 분반에", IsChecked = sectionKeep, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8), ToolTip = "끄면 반마다 학생을 고르게 섞습니다." };
        keep.Click += (_, _) => { sectionKeep = keep.IsChecked == true; sectionExplained = null; Redraw(); };
        conditions.Children.Add(keep); box.Children.Add(conditions);

        var all = Sections.Split(courses, Options());
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(Plain("분반 명단 저장", () => { SaveSections(results, all); return System.Threading.Tasks.Task.CompletedTask; }, this));
        var explain = new Button { Content = "AI로 설명 듣기", Margin = new Thickness(0, 0, 8, 8), ToolTip = "과목별 인원과 분반 수만 AI에 보냅니다. 학생 이름은 보내지 않습니다." };
        explain.Click += async (_, _) =>
        {
            if (!Ai.Ready) new AiWindow(this, "분반 편성 결과를 AI가 설명하게 하려면 먼저 AI를 연결하세요. 과목별 인원과 분반 수만 보내고 학생 이름은 보내지 않습니다.").ShowDialog();
            if (!Ai.Ready) return;
            await Run(async () =>
            {
                try { sectionExplained = await Ai.Ask(Sections.Explain, Sections.Summary(courses, Options()), CancellationToken.None); }
                catch (AiException e) { Say(e.Message, true); return; }
                Redraw();
            }, Ai.Name(Ai.InUse!) + "가 분반 결과를 읽는 중…");
        };
        actions.Children.Add(explain); box.Children.Add(actions);

        if (sectionExplained is not null)
        {
            var said = new TextBox { Text = sectionExplained, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, FontSize = 13 };
            AutomationProperties.SetName(said, "AI 설명");
            var note = AdminUi.Label(Ai.Name(Ai.InUse ?? Ai.Current.Use ?? Ai.ChatGpt) + "의 설명 · 숫자는 아래 표로 확인하세요" + (Ai.InUse == Ai.ChatGpt ? " · Using ChatGPT plan" : ""), "Faint", 11); note.Margin = new Thickness(0, 6, 0, 0);
            var inner = new StackPanel(); inner.Children.Add(said); inner.Children.Add(note);
            var card = new Border { Child = inner, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 14) };
            card.SetResourceReference(Border.BackgroundProperty, "Pale"); box.Children.Add(card);
        }

        var table = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left }; box.Children.Add(table);
        foreach (var group in courses.GroupBy(c => c.Group))
        {
            var first = group.First();
            var heading = AdminUi.Heading(first.Semester == 0 ? first.GroupName : $"{first.GroupName} · {Terms[first.Semester]}", 14); heading.Margin = new Thickness(0, 6, 0, 6); table.Children.Add(heading);
            foreach (var course in group)
            {
                var line = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                foreach (var w in new[] { 150, 86, 104 }) line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                var few = sectionFewest > 0 && course.Pupils.Length > 0 && course.Pupils.Length < sectionFewest;
                var name = AdminUi.Label(course.Name, "Ink", 13); name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = course.Name; name.VerticalAlignment = VerticalAlignment.Center; line.Children.Add(name);
                var count = AdminUi.Label(few ? $"{course.Pupils.Length}명 · 적음" : $"{course.Pupils.Length}명", few ? "Danger" : "Muted", 12, few); count.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(count, 1); line.Children.Add(count);
                var parts = Sections.Split(course, Options());
                var stepper = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(stepper, 2); line.Children.Add(stepper);
                if (course.Pupils.Length > 0)
                {
                    Button Step(string label, int change, string what)
                    {
                        var step = new Button { Content = label, Width = 26, Padding = new Thickness(0), IsEnabled = parts.Length + change >= 1 && parts.Length + change <= course.Pupils.Length };
                        AutomationProperties.SetName(step, course.Name + " 분반 " + what);
                        step.Click += (_, _) => { sectionCounts[Sections.Key(course)] = parts.Length + change; sectionPicked = null; sectionExplained = null; Redraw(); };
                        return step;
                    }
                    stepper.Children.Add(Step("−", -1, "하나 줄이기"));
                    var shown = AdminUi.Label($"{parts.Length}개", "Ink", 13, true); shown.Width = 40; shown.TextAlignment = TextAlignment.Center; shown.VerticalAlignment = VerticalAlignment.Center; stepper.Children.Add(shown);
                    stepper.Children.Add(Step("+", 1, "하나 늘리기"));
                }
                var sizes = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(sizes, 3); line.Children.Add(sizes);
                foreach (var part in parts)
                {
                    var chip = new Button { Content = $"{(char)('A' + part.Index)} {part.Pupils.Length}", Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2), Tag = part.Label };
                    chip.SetResourceReference(StyleProperty, "FilterButton"); AutomationProperties.SetName(chip, part.Label + " 명단");
                    if (part.Label == sectionPicked) { chip.SetResourceReference(BackgroundProperty, "Pale"); chip.SetResourceReference(ForegroundProperty, "Blue"); }
                    var section = part; chip.Click += (_, _) => { sectionPicked = section.Label; Redraw(); };
                    sizes.Children.Add(chip);
                }
                if (parts.Length == 0) sizes.Children.Add(AdminUi.Label("신청 없음", "Faint", 12));
                table.Children.Add(line);
                // The students of the section that was clicked open right under its course, where the eye already is.
                if (parts.FirstOrDefault(p => p.Label == sectionPicked) is { } open) table.Children.Add(Roll(open));
            }
        }
        if (sectionPicked is null) { var hint = AdminUi.Label("A 18 같은 분반 버튼을 누르면 그 분반 학생이 바로 아래에 나옵니다.", "Faint", 12); hint.Margin = new Thickness(0, 8, 0, 0); box.Children.Add(hint); }

        Border Roll(Sections.Section picked)
        {
            var inner = new StackPanel();
            var top = new DockPanel();
            var close = new Button { Content = "닫기", Padding = new Thickness(10, 2, 10, 2) }; close.SetResourceReference(StyleProperty, "SubtleButton"); AutomationProperties.SetName(close, picked.Label + " 명단 닫기");
            close.Click += (_, _) => { sectionPicked = null; Redraw(); }; DockPanel.SetDock(close, Dock.Right); top.Children.Add(close);
            top.Children.Add(AdminUi.Heading($"{picked.Label} · {picked.Pupils.Length}명", 14)); inner.Children.Add(top);
            var homerooms = AdminUi.Label(string.Join(" · ", picked.Pupils.GroupBy(p => p.ClassNo).Select(g => $"{g.Key}반 {g.Count()}명")), "Muted", 12);
            homerooms.TextWrapping = TextWrapping.Wrap; homerooms.Margin = new Thickness(0, 2, 0, 8); inner.Children.Add(homerooms);
            var names = new WrapPanel();
            foreach (var pupil in picked.Pupils) { var n = AdminUi.Label($"{pupil.ClassNo}반 {pupil.Number}번 {pupil.Name}", "Ink", 12); n.Width = 140; n.Margin = new Thickness(0, 2, 0, 2); names.Children.Add(n); }
            inner.Children.Add(names);
            var card = new Border { Child = inner, Padding = new Thickness(14, 10, 14, 12), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 4, 0, 8) };
            card.SetResourceReference(Border.BackgroundProperty, "Pale"); return card;
        }
    }

    void SaveSections(Results results, Sections.Section[] sections) =>
        Save($"{results.Round.Title} 분반 편성", "학년,반,번호,이름,선택군,학기,과목,분반",
            sections.SelectMany(s => s.Pupils.Select(p => new object[] { results.Round.Grade, p.ClassNo, p.Number, p.Name, s.Course.GroupName, Terms[s.Course.Semester], s.Course.Name, s.Label }))
                .OrderBy(r => (int)r[1]).ThenBy(r => (int)r[2]), "분반 명단을 저장했습니다.");
}
