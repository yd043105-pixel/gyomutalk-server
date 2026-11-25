using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SchoolMessenger.Desktop;

// The AI only puts in order the ways to empty a period that the program has already found and checked against every rule
// (no teacher in two places, no marked period, nothing already begun); it never makes a trade itself. Teachers' names
// leave the PC as 교사A, 교사B…, so only days, periods, classes and subjects are sent.
static class SwapAdvice
{
    public const string Instructions =
        "너는 학교 수업 교체를 돕는다. 아래 후보는 프로그램이 교사 겹침, 수업 불가 시간, 이미 지난 수업 규칙을 모두 확인한 교체안이다. " +
        "선생님의 요청을 가장 잘 따르는 순서로 후보 번호를 늘어놓아라. 요청에 특별한 말이 없으면 같은 날 교체와 바꾸는 수업이 적은 안을 앞에 둔다. " +
        "새 교체를 만들거나 후보를 고치지 마라. 첫 줄에는 번호만 쉼표로 적고(예: 2,1,3), 둘째 줄부터 한국어로 두세 문장 이유를 적어라. " +
        "요청을 지키는 후보가 없으면 그렇다고 말하라.";

    // Stand-in names, the same one for a teacher everywhere in one request.
    public sealed class Names(string asking)
    {
        readonly Dictionary<string, string> codes = [];
        public string Of(string name)
        {
            if (name == asking) return "요청한 선생님";
            if (codes.TryGetValue(name, out var code)) return code;
            var n = codes.Count; code = "교사" + (char)('A' + n % 26) + (n >= 26 ? (n / 26 + 1).ToString() : "");
            return codes[name] = code;
        }
    }

    public static string Prompt(string request, string vacate, IReadOnlyList<(bool SameDay, string[] Lines)> options)
    {
        var text = new StringBuilder();
        text.AppendLine("선생님 요청: " + (request.Trim().Length > 0 ? request.Trim() : "(없음)"));
        text.AppendLine("비울 시간: " + vacate);
        text.AppendLine("후보:");
        for (var i = 0; i < options.Count; i++)
        {
            text.AppendLine($"{i + 1}) [{(options[i].SameDay ? "같은 날" : "다른 날 포함")}]");
            foreach (var line in options[i].Lines) text.AppendLine("   " + line);
        }
        return text.ToString();
    }

    // The order the AI chose (every candidate exactly once, the ones it left out in the program's order) and what it said.
    public static (int[] Order, string Reason) Read(string answer, int count)
    {
        var lines = answer.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries).Where(l => l.Length > 0).ToArray();
        var first = lines.FirstOrDefault() ?? "";
        var numbers = first.Split([',', ' ', '·', '>', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t.Trim('.', ')', '번'), out var n) ? n - 1 : -1).ToArray();
        var listed = numbers.Length > 0 && numbers.All(n => n >= 0);
        var order = (listed ? numbers.Where(n => n < count) : []).Distinct().ToList();
        order.AddRange(Enumerable.Range(0, count).Where(n => !order.Contains(n)));
        var reason = string.Join(" ", listed ? lines.Skip(1) : lines);
        return (order.ToArray(), reason.Length > 600 ? reason[..600] + "…" : reason);
    }
}
