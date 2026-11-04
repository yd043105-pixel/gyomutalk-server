using SchoolMessenger.Desktop;

// Requests as they appear in real office notices, dated on a fixed Wednesday in Korean time.
var sent=new DateTimeOffset(2026,11,4,9,0,0,TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
var found=new[]{"담임 선생님께서는 11월 7일까지 학급별 인솔 명단을 제출해 주세요.","내일 오후 3시에 교무회의가 있습니다. 각 부서에서는 안건을 준비해 주세요.","다음 주 수요일까지 과학실을 열어 주세요.\n11월 19일(목) 오후 2시까지 행정실로 서류를 보내 주세요.\n자세한 내용은 첨부 파일을 참고하시기 바랍니다."}.SelectMany(body=>TaskCandidates.Candidates(body,sent)).ToArray();
string? Due(string title)=>found.FirstOrDefault(c=>c.Title==title)?.DueDate;
void Require(string? actual,string expected){if(actual!=expected)throw new Exception($"expected {expected}, got {actual}: "+string.Join(" / ",found.Select(c=>c.Title)));}
Require(Due("학급별 인솔 명단 제출"),"2026-11-07");
Require(Due("안건 준비"),"2026-11-05");
Require(Due("과학실 열기"),"2026-11-11");
Require(Due("행정실로 서류 보내기"),"2026-11-19");
if(found.Any(c=>c.Title.Contains("참고")))throw new Exception("a pointer to an attachment became a task");
Console.WriteLine("ok");
