using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

/// <summary>Reads the NEIS timetable workbooks (base, classroom and changed lessons) without executing Excel, formulas or external connections.</summary>
public static class TimetableWorkbook
{
    static readonly Regex ClassLabel=new(@"^(?:\d+\s*\()?\s*(\d+)\s*학년\s*(\d+)\s*반\s*\)?$",RegexOptions.CultureInvariant);
    static readonly Regex DateLabel=new(@"(?<y>\d{4})[.\-/년]\s*(?<m>\d{1,2})[.\-/월]\s*(?<d>\d{1,2})",RegexOptions.CultureInvariant);
    static readonly string[] Days=["월","화","수","목","금","토","일"];
    const int MaxCells=40_000;
    public static TimetableWorkbookData Read(string path,string kind)
    {
        if(kind is not("auto"or"base"or"daily"or"changes"))throw new FilePolicyException("시간표 종류를 확인하세요.");
        try{return ReadCore(path,kind);}catch(FilePolicyException){throw;}catch(Exception e)when(e is InvalidDataException or XmlException or ArgumentException or FormatException or OverflowException or InvalidOperationException)
        {throw new FilePolicyException("손상되었거나 지원하지 않는 시간표 엑셀입니다.");}
    }
    static TimetableWorkbookData ReadCore(string path,string kind)
    {
        using var zip=ZipFile.OpenRead(path);
        if(zip.Entries.Count>256||zip.Entries.Sum(e=>e.Length)>50_000_000||zip.Entries.Any(e=>e.Length>12_000_000)||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=zip.Entries.Count)
            throw new FilePolicyException("엑셀 압축 크기 또는 구성 제한을 넘었습니다.",413);
        if(zip.Entries.Any(e=>e.FullName.Contains("externalLinks",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith("vbaProject.bin",StringComparison.OrdinalIgnoreCase)))throw new FilePolicyException("외부 연결·매크로가 있는 파일은 사용할 수 없습니다.");
        XDocument Xml(string name)
        {
            var entry=zip.GetEntry(name)??throw new FilePolicyException("엑셀 구성 파일을 찾을 수 없습니다.");
            using var stream=entry.Open();using var reader=XmlReader.Create(stream,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=12_000_000});return XDocument.Load(reader);
        }
        var types=Xml("[Content_Types].xml");if(types.Descendants().Attributes("ContentType").Any(a=>a.Value.Contains("macroEnabled",StringComparison.OrdinalIgnoreCase)))throw new FilePolicyException("매크로 파일은 사용할 수 없습니다.");
        string Text(XElement e)=>string.Concat(e.Descendants().Where(v=>v.Name.LocalName=="t").Select(v=>v.Value));
        var shared=zip.GetEntry("xl/sharedStrings.xml") is null?[]:Xml("xl/sharedStrings.xml").Descendants().Where(e=>e.Name.LocalName=="si").Select(Text).ToArray();
        var workbook=Xml("xl/workbook.xml");var relationships=Xml("xl/_rels/workbook.xml.rels").Descendants().Where(e=>e.Name.LocalName=="Relationship").ToArray();
        if(relationships.Any(r=>(string?)r.Attribute("TargetMode")=="External"))throw new FilePolicyException("외부 연결이 있는 엑셀은 사용할 수 없습니다.");
        var cells=new List<TimetableRawCell>();int year=0,semester=0;string? date=null;var seenSheets=0;
        foreach(var sheet in workbook.Descendants().Where(e=>e.Name.LocalName=="sheet"))
        {
            if(++seenSheets>30)throw new FilePolicyException("시간표 시트가 너무 많습니다.");
            var relationship=relationships.SingleOrDefault(r=>(string?)r.Attribute("Id")==sheet.Attributes().FirstOrDefault(a=>a.Name.LocalName=="id")?.Value);
            var target=(string?)relationship?.Attribute("Target")??"";
            var file=target.StartsWith("/",StringComparison.Ordinal)?target.TrimStart('/') : "xl/"+target;
            if(!file.StartsWith("xl/worksheets/",StringComparison.Ordinal)||file.Contains("..")||file.Contains('\\')||file.Contains(':'))throw new FilePolicyException("엑셀 시트 경로가 올바르지 않습니다.");
            var xml=Xml(file);var values=new Dictionary<(int Row,int Col),string>();
            foreach(var cell in xml.Descendants().Where(e=>e.Name.LocalName=="c"))
            {
                if(cell.Elements().Any(e=>e.Name.LocalName=="f"))throw new FilePolicyException("수식 셀은 값으로 저장한 뒤 올려주세요: "+cell.Attribute("r")?.Value);
                var at=Coordinate((string?)cell.Attribute("r")??"");
                if(at.Row>1000||at.Col>300||values.Count>=MaxCells)throw new FilePolicyException("시간표 행·열 제한을 넘었습니다.",413);
                var raw=cell.Elements().FirstOrDefault(e=>e.Name.LocalName=="v")?.Value??"";
                var value=(string?)cell.Attribute("t") switch{"s"=>int.TryParse(raw,out var n)&&n>=0&&n<shared.Length?shared[n]:throw new FilePolicyException("엑셀 문자열 참조 오류입니다."),"inlineStr"=>Text(cell),_=>raw};
                if(value.Length>500||!values.TryAdd(at,value.Trim()))throw new FilePolicyException("중복되거나 너무 긴 셀입니다.");
            }
            var merges=xml.Descendants().Where(e=>e.Name.LocalName=="mergeCell").Select(e=>Range((string?)e.Attribute("ref")??"")).ToArray();
            if(merges.Length>4000)throw new FilePolicyException("병합 셀이 너무 많습니다.");
            string Get(int r,int c){if(values.TryGetValue((r,c),out var v)&&v.Length>0)return v;var m=merges.FirstOrDefault(m=>r>=m.Start.Row&&r<=m.End.Row&&c>=m.Start.Col&&c<=m.End.Col);return m==default?"":values.GetValueOrDefault(m.Start,"");}
            var heading=values.Values.Select(v=>Regex.Match(v,@"(\d{4})\s*(?:학년도|년도|년)\s*(\d)\s*학기")).FirstOrDefault(m=>m.Success);
            if(heading is null)continue;
            var y=int.Parse(heading.Groups[1].Value);var s=int.Parse(heading.Groups[2].Value);
            if(y is <2000 or >2100||s is <1 or >2||year!=0&&(year!=y||semester!=s))throw new FilePolicyException("학년도·학기가 서로 다릅니다.");year=y;semester=s;
            // The title names the file; a workbook without one is told apart by its headers.
            if(kind=="auto")kind=values.Values.Any(v=>v.Contains("(변경수업)"))?"changes":values.Values.Any(v=>v=="일자")?"daily":"base";
            if(kind=="base")
            {
                var dayHeaders=values.Where(v=>v.Key.Row<=15&&Days.Contains(v.Value)).OrderBy(v=>v.Key.Col).ToArray();
                if(dayHeaders.Length is <5 or >7||dayHeaders.Select(d=>d.Key.Row).Distinct().Count()!=1||dayHeaders.Select(d=>d.Value).Distinct().Count()!=dayHeaders.Length)throw new FilePolicyException("월~금 요일 머리글을 확인하세요.");
                var headerRow=dayHeaders[0].Key.Row;
                foreach(var row in values.Keys.Select(k=>k.Row).Distinct().Where(r=>r>headerRow+1).Order())
                {
                    var labels=values.Where(v=>v.Key.Row==row&&v.Key.Col<=8).Select(v=>ClassLabel.Match(v.Value)).Where(m=>m.Success).ToArray();if(labels.Length==0)continue;if(labels.Length!=1)throw new FilePolicyException("학급 행이 중복되었습니다.");
                    for(var i=0;i<dayHeaders.Length;i++)
                    {
                        var from=dayHeaders[i].Key.Col;var to=i+1<dayHeaders.Length?dayHeaders[i+1].Key.Col:values.Keys.Max(k=>k.Col)+1;
                        var periods=values.Where(v=>v.Key.Row==headerRow+1&&v.Key.Col>=from&&v.Key.Col<to&&Period(v.Value)>0).OrderBy(v=>v.Key.Col).ToArray();
                        if(periods.Length==0||periods.Select(p=>Period(p.Value)).Distinct().Count()!=periods.Length)throw new FilePolicyException("교시 머리글이 없거나 중복입니다.");
                        foreach(var p in periods)Add(labels[0],Array.IndexOf(Days,dayHeaders[i].Value)+1,Period(p.Value),Get(row,p.Key.Col),Address(row,p.Key.Col));
                    }
                }
            }
            else
            {
                // NEIS prints one table per page: each page repeats its headers and may continue a day from the page before.
                var pages=values.Where(v=>v.Value=="교시").OrderBy(v=>v.Key.Row).ToArray();if(pages.Length==0)throw new FilePolicyException("일자·교시 머리글을 확인하세요.");
                var captionText="";
                for(var page=0;page<pages.Length;page++)
                {
                    var periodHeader=pages[page];var dateHeader=values.Where(v=>v.Value=="일자"&&v.Key.Row==periodHeader.Key.Row).Select(v=>v.Key).FirstOrDefault();if(dateHeader==default)throw new FilePolicyException("일자·교시 머리글을 확인하세요.");
                    var next=page+1<pages.Length?pages[page+1].Key.Row:int.MaxValue;
                    var classHeaders=values.Where(v=>v.Key.Row>periodHeader.Key.Row&&v.Key.Row<=periodHeader.Key.Row+3&&ClassLabel.IsMatch(v.Value)).ToArray();
                    if(classHeaders.Length==0||classHeaders.Select(c=>c.Key.Row).Distinct().Count()!=1)throw new FilePolicyException("일자별 시간표의 학급 머리글을 확인하세요.");
                    foreach(var p in values.Where(v=>v.Key.Col==periodHeader.Key.Col&&v.Key.Row>classHeaders[0].Key.Row&&v.Key.Row<next&&Period(v.Value)>0).OrderBy(v=>v.Key.Row))
                    {
                        var caption=Get(p.Key.Row,dateHeader.Col);if(caption.Length>0)captionText=caption;
                        var match=DateLabel.Match(captionText);if(!match.Success)throw new FilePolicyException("수업일을 읽을 수 없습니다: "+Address(p.Key.Row,dateHeader.Col));
                        var day=new DateOnly(int.Parse(match.Groups["y"].Value),int.Parse(match.Groups["m"].Value),int.Parse(match.Groups["d"].Value));
                        if(day.Year!=year&&day.Year!=year+1)throw new FilePolicyException("수업일이 학년도와 다릅니다: "+Address(p.Key.Row,dateHeader.Col));
                        if(Regex.IsMatch(captionText,@"[월화수목금토일]요일")&&!captionText.Contains(Days[((int)day.DayOfWeek+6)%7]+"요일"))throw new FilePolicyException("수업일의 날짜와 요일이 다릅니다.");
                        var when=day.ToString("yyyy-MM-dd");
                        foreach(var c in classHeaders){var text=Get(p.Key.Row,c.Key.Col);if(kind=="changes"&&text.Length==0)continue;Add(ClassLabel.Match(c.Value),((int)day.DayOfWeek+6)%7+1,Period(p.Value),text,Address(p.Key.Row,c.Key.Col),when);}
                    }
                }
            }
            void Add(Match label,int day,int period,string value,string address,string? when=null)
            {
                var grade=int.Parse(label.Groups[1].Value);var number=int.Parse(label.Groups[2].Value);if(grade is <1 or >3||number is <1 or >99)throw new FilePolicyException("학년·반 번호를 확인하세요: "+address);
                var lesson=Regex.Match(value,@"^(.*)\(([^()]*)\)$",RegexOptions.Singleline);var subject=lesson.Success?lesson.Groups[1].Value.Trim():value;var teacher=lesson.Success?lesson.Groups[2].Value.Trim():"";
                if(value.Length>0&&(!lesson.Success||subject.Length==0||teacher.Length==0))throw new FilePolicyException("과목(교사) 양식을 확인하세요: "+address);
                if(subject.Length>200||teacher.Length>100)throw new FilePolicyException("과목·교사 이름이 너무 깁니다: "+address);
                cells.Add(new($"{grade}학년 {number}반",grade,number,day,period,subject,teacher,(string?)sheet.Attribute("name")+"!"+address,when));
            }
        }
        if(kind=="auto")throw new FilePolicyException("시간표 머리글을 찾을 수 없습니다.");
        var dates=cells.Select(c=>c.Date).Where(d=>d is not null).Distinct().ToArray();if(dates.Length==1)date=dates[0];
        if(kind!="changes"&&cells.Count==0||cells.Count>10_000||kind=="base"&&cells.All(c=>c.Subject.Length==0)||cells.GroupBy(c=>(c.ClassKey,c.Date,c.Day,c.Period)).Any(g=>g.Count()>1)||kind=="daily"&&dates.Length==0)throw new FilePolicyException("시간표가 비어 있거나 학급·교시가 중복되었습니다.");
        return new(kind,year,semester,date,cells.ToArray());
    }
    static int Period(string value)=>decimal.TryParse(value.Replace("교시",""),NumberStyles.Float,CultureInfo.InvariantCulture,out var p)&&p==decimal.Truncate(p)&&p is >=1 and <=12?(int)p:0;
    static (int Row,int Col) Coordinate(string value){var match=Regex.Match(value,@"^([A-Z]{1,3})([1-9]\d{0,3})$");if(!match.Success)throw new FilePolicyException("셀 주소 오류입니다.");var col=0;foreach(var c in match.Groups[1].Value)col=checked(col*26+c-'A'+1);return(int.Parse(match.Groups[2].Value),col);}
    static ((int Row,int Col) Start,(int Row,int Col) End) Range(string value){var parts=value.Split(':');if(parts.Length!=2)throw new FilePolicyException("병합 범위 오류입니다.");var a=Coordinate(parts[0]);var b=Coordinate(parts[1]);if(a.Row>b.Row||a.Col>b.Col||b.Row>1000||b.Col>300)throw new FilePolicyException("병합 범위 제한을 넘었습니다.");return(a,b);}
    static string Address(int r,int c){var s="";while(c>0){c--;s=(char)('A'+c%26)+s;c/=26;}return s+r;}
}
