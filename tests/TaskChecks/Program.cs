using SchoolMessenger.Desktop;

// Requests as they appear in real office notices, dated on a fixed Wednesday in Korean time.
var sent=new DateTimeOffset(2026,11,4,9,0,0,TimeSpan.FromHours(9)).ToUnixTimeMilliseconds();
var found=new[]{"담임 선생님께서는 11월 7일까지 학급별 인솔 명단을 제출해 주세요.","내일 오후 3시에 교무회의가 있습니다. 각 부서에서는 안건을 준비해 주세요.","다음 주 수요일까지 과학실을 열어 주세요.\n11월 19일(목) 오후 2시까지 행정실로 서류를 보내 주세요.\n자세한 내용은 첨부 파일을 참고하시기 바랍니다.",
  "금요일 오전까지 학급별 명단을 올려 주세요.","11월 10일 퇴근 전까지 출장 보고서를 제출해 주세요.","오늘 오전 중으로 회신해 주세요.","11월 12일 10시까지 설문지를 입력해 주세요.","11월 13일 4시 반까지 상담 일지를 등록해 주세요."}.SelectMany(body=>TaskCandidates.Candidates(body,sent)).ToArray();
TaskCandidate? Find(string title)=>found.FirstOrDefault(c=>c.Title==title);
void Require(string title,string? date,string? time){var c=Find(title);if(c is null||c.DueDate!=date||c.DueTime!=time)throw new Exception($"{title}: expected {date} {time}, got {c?.DueDate} {c?.DueTime}: "+string.Join(" / ",found.Select(c=>c.Title)));}
Require("학급별 인솔 명단 제출","2026-11-07",null);
// The meeting time carries over to the request that depends on it.
Require("안건 준비","2026-11-05","15:00");
Require("과학실 열기","2026-11-11",null);
Require("행정실로 서류 보내기","2026-11-19","14:00");
// "오전까지" is due by noon and "퇴근 전" by 17:00; neither stays in the title.
Require("학급별 명단 올리기","2026-11-06","12:00");
Require("출장 보고서 제출","2026-11-10","17:00");
Require("회신","2026-11-04","12:00");
// An hour without 오전/오후 that falls in the afternoon of a school day means the afternoon.
Require("설문지 입력","2026-11-12","10:00");
Require("상담 일지 등록","2026-11-13","16:30");
if(found.Any(c=>c.Title.Contains("참고")))throw new Exception("a pointer to an attachment became a task");
if(TaskCandidates.Candidates("오전 중으로 회신해 주세요.",sent).Single().DueTime is not null)throw new Exception("a time without a date became a deadline");
Console.WriteLine("ok");
