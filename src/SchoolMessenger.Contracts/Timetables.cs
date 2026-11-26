namespace SchoolMessenger.Contracts;

public sealed record SchoolClass(string Id,string Name,int Grade);

public record TimetableRawCell(string ClassKey,int Grade,int ClassNumber,int Day,int Period,string Subject,string Teacher,string Address,string? Date=null);
public record TimetableWorkbookData(string Kind,int Year,int Semester,string? Date,TimetableRawCell[] Cells);
// Subject: the account is for this name only where it teaches this subject (same-named teachers); without it, for the name everywhere.
public record TimetableLink(string Source,string Id,string? Subject=null);
public record TimetablePreviewRequest(string DraftId,string Start,string End,TimetableLink[] Classes,TimetableLink[] Teachers,string[]? DraftIds=null);
public record TimetableCommit(string CandidateId,string ClientId,string[]? SwapIds=null);
public record TimetableCell(string ClassId,string ClassName,int Day,int Period,string Subject,string TeacherId,string TeacherName);
public record TimetableBatch(string Id,long Revision,string Owner,string ClientId,string Kind,int Year,int Semester,string Start,string End,string? Date,string FileName,long PublishedAt,bool Withdrawn,TimetableCell[] Cells,string? PublicationCandidateId=null,string[]? ApprovedSwapIds=null);
public record TimetableSlot(string ClassId,string ClassName,int Period,string Subject,string TeacherId,string TeacherName,bool Changed,string BeforeSubject,string BeforeTeacher,string Source);
public record TimetableDay(string Date,long Revision,long PublishedAt,string[] MissingClasses,TimetableSlot[] Slots);
public record TimetableNotice(long Revision,string UserId,string Date,string[] ClassIds,string Title,long CreatedAt,long? ReadAt=null);
public record TimetableHomeroom(string ClassId,string TeacherId);
public record TimetableDelivery(TimetableBatch Batch,TimetableHomeroom[] Homerooms,TimetableNotice[] Notices);

public sealed record TimetablePosition(string Date, string ClassId, int Period);
public sealed record TimetableExchange(TimetablePosition First, TimetablePosition Second);
public sealed record TimetableSwapPlanRequest(string[] Dates, string[]? Dependencies, TimetableExchange[]? Exchanges = null);
// AllowOverlap: also offer trades that leave a teacher in two classes at once for now, to be untangled by the next trade.
public sealed record TimetableSwapOptionsRequest(string[] Dates, string[]? Dependencies, TimetableExchange[]? Exchanges, TimetablePosition Pick, bool AllowOverlap = false);
// One lesson the picked lesson can trade places with, and every exchange that trade needs (bundled classes move along).
// Overlap: after this trade a teacher still has two lessons at one time, so another trade is needed before sending.
public sealed record TimetableSwapOption(TimetablePosition Target, TimetableExchange[] Exchanges, bool Overlap = false);
// A period a teacher has said they cannot teach: nobody's request may move a lesson of theirs into it.
public sealed record TimetableUnavailable(string TeacherId, string TeacherName, string Date, int Period);
public sealed record TimetableTime(string Date, int Period);
// Replaces one teacher's marks on these dates: the signed-in teacher's own, or (TeacherId) any teacher's for the timetable manager.
public sealed record TimetableUnavailableUpdate(string[] Dates, TimetableTime[] Times, string? TeacherId = null);
// The periods the signed-in teacher needs free; the server answers with whole requests that empty them.
public sealed record TimetableSuggestionRequest(string[] Dates, string[]? Dependencies, TimetableExchange[]? Exchanges, TimetableTime[] Vacate);
public sealed record TimetableSuggestion(TimetableExchange[] Exchanges, bool SameDay);
// Lessons that run at the same time for several classes: wherever these (class, subject) lessons share a period, they move together.
public sealed record TimetableBundleMember(string ClassId, string Subject);
public sealed record TimetableBundleRule(string Id, string Name, TimetableBundleMember[] Members);
public sealed record TimetableBaseLesson(string ClassId, string ClassName, int Grade, string Subject, string Teachers, int Hours);
public sealed record TimetableSwapCreate(string Id, long Revision, TimetableExchange[] Exchanges, string[]? Dependencies, string? Reason);
public sealed record TimetableSwapDecision(string Status);
public sealed record TimetablePlanSlot(TimetablePosition Position, TimetableSlot Lesson, bool Mine, string? BundleName = null);
// Overlaps: lessons the request being drafted leaves at the same time as another lesson of their teacher.
public sealed record TimetableSwapPlan(long Revision, TimetablePlanSlot[] Slots, TimetablePosition[]? Overlaps = null, TimetableUnavailable[]? Unavailable = null, int? SwapWeeks = null);
// How many school weeks apart two traded lessons may be (0: the same week only). The timetable manager sets it for the school.
public sealed record TimetableSwapRange(int Weeks);
public sealed record TimetableSwapChange(TimetablePosition Position, TimetableSlot Before, TimetableSlot After);
public sealed record TimetableSwap(
    string Id, string Owner, string OwnerName, string Status, string Reason, long CreatedAt,
    string[] Dependencies, TimetableExchange[] Exchanges, TimetableSwapChange[] Changes,
    string? Reviewer = null, string? ReviewerName = null, long? ReviewedAt = null, long? PublishedRevision = null);
public sealed record TimetableBundle(string Id, string Name, string Kind, TimetablePosition[] Slots);
