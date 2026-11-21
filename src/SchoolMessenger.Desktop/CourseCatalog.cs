using System;
using System.Collections.Generic;
using System.Linq;

namespace SchoolMessenger.Desktop;

// 2022 개정 고등학교 교육과정 보통교과 과목(공통·일반선택·진로선택·융합선택). Typing one of these names fills in its 교과(군) and 유형;
// a course the school made up itself is typed in and the teacher picks both by hand.
static class CourseCatalog
{
    public static readonly string[] Areas = ["국어", "수학", "영어", "사회", "과학", "체육", "예술", "기술가정/정보", "제2외국어/한문", "교양"];
    public static readonly string[] Types = ["공통", "일반선택", "진로선택", "융합선택"];

    const string Data = """
        국어|공통|공통국어1, 공통국어2
        국어|일반선택|화법과 언어, 독서와 작문, 문학
        국어|진로선택|주제 탐구 독서, 문학과 영상, 직무 의사소통
        국어|융합선택|독서 토론과 글쓰기, 매체 의사소통, 언어생활 탐구
        수학|공통|공통수학1, 공통수학2, 기본수학1, 기본수학2
        수학|일반선택|대수, 미적분Ⅰ, 확률과 통계
        수학|진로선택|기하, 미적분Ⅱ, 경제 수학, 인공지능 수학, 직무 수학
        수학|융합선택|수학과 문화, 실용 통계, 수학과제 탐구
        영어|공통|공통영어1, 공통영어2, 기본영어1, 기본영어2
        영어|일반선택|영어Ⅰ, 영어Ⅱ, 영어 독해와 작문
        영어|진로선택|영미 문학 읽기, 영어 발표와 토론, 심화 영어, 심화 영어 독해와 작문, 직무 영어
        영어|융합선택|실생활 영어 회화, 미디어 영어, 세계 문화와 영어
        사회|공통|한국사1, 한국사2, 통합사회1, 통합사회2
        사회|일반선택|세계시민과 지리, 세계사, 사회와 문화, 현대사회와 윤리
        사회|진로선택|한국지리 탐구, 도시의 미래 탐구, 동아시아 역사 기행, 정치, 법과 사회, 경제, 윤리와 사상, 인문학과 윤리, 국제 관계의 이해
        사회|융합선택|여행지리, 역사로 탐구하는 현대 세계, 사회문제 탐구, 금융과 경제생활, 윤리문제 탐구, 기후변화와 지속가능한 세계
        과학|공통|통합과학1, 통합과학2, 과학탐구실험1, 과학탐구실험2
        과학|일반선택|물리학, 화학, 생명과학, 지구과학
        과학|진로선택|역학과 에너지, 전자기와 양자, 물질과 에너지, 화학 반응의 세계, 세포와 물질대사, 생물의 유전, 지구시스템과학, 행성우주과학
        과학|융합선택|과학의 역사와 문화, 기후변화와 환경생태, 융합과학 탐구
        체육|일반선택|체육1, 체육2
        체육|진로선택|운동과 건강, 스포츠 문화, 스포츠 과학
        체육|융합선택|스포츠 생활1, 스포츠 생활2
        예술|일반선택|음악, 미술, 연극
        예술|진로선택|음악 연주와 창작, 음악 감상과 비평, 미술 창작, 미술 감상과 비평
        예술|융합선택|음악과 미디어, 미술과 매체
        기술가정/정보|일반선택|기술·가정, 정보
        기술가정/정보|진로선택|로봇과 공학세계, 생활과학 탐구, 인공지능 기초, 데이터 과학
        기술가정/정보|융합선택|창의 공학 설계, 지식 재산 일반, 생애 설계와 자립, 아동발달과 부모, 소프트웨어와 생활
        제2외국어/한문|일반선택|독일어, 프랑스어, 스페인어, 중국어, 일본어, 러시아어, 아랍어, 베트남어, 한문
        제2외국어/한문|진로선택|독일어 회화, 프랑스어 회화, 스페인어 회화, 중국어 회화, 일본어 회화, 러시아어 회화, 아랍어 회화, 베트남어 회화, 심화 독일어, 심화 프랑스어, 심화 스페인어, 심화 중국어, 심화 일본어, 심화 러시아어, 심화 아랍어, 심화 베트남어, 한문 고전 읽기
        제2외국어/한문|융합선택|독일어권 문화, 프랑스어권 문화, 스페인어권 문화, 중국 문화, 일본 문화, 러시아 문화, 아랍 문화, 베트남 문화, 언어생활과 한자
        교양|일반선택|진로와 직업, 생태와 환경
        교양|진로선택|인간과 철학, 논리와 사고, 인간과 심리, 교육의 이해, 삶과 종교, 보건
        교양|융합선택|인간과 경제활동, 논술
        """;

    // The drop-down list shows the kind next to each name; the box itself takes only the name.
    public sealed record Course(string Name, string Area, string Type) { public override string ToString() => $"{Name}   ({Area} · {Type})"; }

    public static readonly Course[] All = Data.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('|')).SelectMany(p => p[2].Split(',', StringSplitOptions.TrimEntries).Select(n => new Course(n, p[0], p[1]))).ToArray();

    static readonly Dictionary<string, Course> ByKey = All.ToDictionary(c => Key(c.Name));

    // "미적분 1", "미적분I", "기술가정" all find the same course.
    static string Key(string name) => new string(name.Where(ch => !char.IsWhiteSpace(ch) && ch != '·' && ch != '.').ToArray())
        .Replace('Ⅰ', '1').Replace('Ⅱ', '2').Replace("II", "2").Replace("I", "1");

    public static Course? Find(string name) => ByKey.GetValueOrDefault(Key(name));
}
