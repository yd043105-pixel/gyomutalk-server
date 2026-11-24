using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SchoolMessenger.Desktop;

// 보내기 전 말투 점검, done on this PC without AI: discriminatory words, old administrative words with plainer ones,
// stock AI phrasing, and dates written as 2026-11-20. Written for 교무톡 after the categories of nmsw
// (github.com/chrisryugj/nmsw, which has no license, so none of its code or word lists are copied). Plainer words follow
// the 국립국어원 다듬은 말 and the government 행정용어 순화어 list; the date form follows 「행정업무의 운영 및 혁신에 관한 규정」 제7조.
public static class StyleCheck
{
    public sealed record Finding(string Kind, string Found, string Instead);

    static readonly (string Kind, Regex Pattern, string Instead)[] Rules =
    [
        ("차별 표현", W("학부형"), "학부모"),
        ("차별 표현", W("결손\\s*가정"), "한부모 가정(또는 가족 형태를 쓰지 않기)"),
        ("차별 표현", W("장애우"), "장애인"),
        ("차별 표현", W("미망인"), "고인의 배우자"),
        ("차별 표현", W("살색"), "살구색"),
        ("차별 표현", W("정상인"), "비장애인"),
        ("어려운 말", W("익일"), "다음 날"),
        ("어려운 말", W("금일"), "오늘"),
        ("어려운 말", W("명일"), "내일"),
        ("어려운 말", W("상기(?=\\s*(의|와|내용|사항|건|일정))"), "위"),
        ("어려운 말", W("취합"), "모음 · 모아"),
        ("어려운 말", W("송부"), "보냄 · 보내"),
        ("어려운 말", W("제고"), "높임 · 높이기"),
        ("어려운 말", W("만전을\\s*기"), "빈틈없이 하"),
        ("어려운 말", W("시건\\s*장치"), "잠금장치"),
        ("어려운 말", W("사료됩니다"), "생각합니다"),
        ("어려운 말", W("하오니"), "하니"),
        ("어려운 말", W("필히"), "반드시"),
        ("AI 말투", new Regex("—|―", RegexOptions.CultureInvariant), "마침표로 끊거나 쉼표"),
        ("AI 말투", W("결론적으로"), "지우고 바로 쓰기"),
        ("AI 말투", W("(획기적|혁신적|비약적)(으로|인)"), "숫자·사실로"),
        ("AI 말투", W("다양한\\s*인사이트|시너지를?\\s*(내|창출)"), "얻은 점·함께 낸 결과를 그대로"),
        ("AI 말투", W("단순한\\s*\\S+\\s*(을|를)\\s*넘어"), "무엇을 하는지 그대로"),
        ("AI 말투", W("(궁금한|문의할)\\s*(점|사항)이\\s*있으(시면|면)\\s*언제든(지)?"), "지우거나 ‘문의는 ○○에게’"),
        ("AI 말투", W("도움이\\s*되(셨|었)(길|으면)\\s*(합니다|좋겠습니다|바랍니다)"), "지우기"),
    ];

    static Regex W(string pattern) => new(pattern, RegexOptions.CultureInvariant);
    static readonly Regex IsoDate = new(@"(?<!\d)(20\d\d)-(\d{1,2})-(\d{1,2})(?!\d)", RegexOptions.CultureInvariant);

    public static Finding[] Check(string text)
    {
        var found = Rules.SelectMany(r => r.Pattern.Matches(text).Select(m => new Finding(r.Kind, m.Value.Trim(), r.Instead))).ToList();
        foreach (Match m in IsoDate.Matches(text))
        {
            if (!DateTime.TryParseExact($"{m.Groups[1].Value}-{int.Parse(m.Groups[2].Value):00}-{int.Parse(m.Groups[3].Value):00}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            found.Add(new("날짜 표기", m.Value, day.ToString("yyyy. M. d.", CultureInfo.InvariantCulture) + "(" + day.ToString("ddd", CultureInfo.GetCultureInfo("ko-KR")) + ")"));
        }
        return found.DistinctBy(f => (f.Kind, f.Found)).ToArray();
    }

    // The same advice for the AI, so ‘AI로 다듬기’ does not bring in what this check would flag.
    public const string Guide = " 다음은 쓰지 마세요: 줄표(—), 말줄임표, ‘좋은 질문이에요’·‘도움이 되셨길 바랍니다’ 같은 챗봇·아첨 말투, ‘획기적으로’·‘혁신적인’ 같은 과장어휘(숫자·사실로 쓰기), ‘결론적으로’·‘~할 때입니다’ 같은 결산 문장, ‘단순한 X를 넘어’, 비유 동사. 행정 한자어는 쉬운 말로 바꾸세요(익일→다음 날, 금일→오늘, 취합→모음, 송부→보냄, 제고→높임, 사료됩니다→생각합니다). 학부형·결손 가정·장애우 같은 차별 표현은 학부모·한부모 가정·장애인으로 쓰세요. 날짜는 ‘2026. 11. 20.(금)’, 시각은 ‘14:00’처럼 씁니다.";
}
