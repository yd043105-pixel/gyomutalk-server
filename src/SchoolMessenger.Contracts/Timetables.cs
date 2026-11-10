namespace SchoolMessenger.Contracts;

public sealed record SchoolClass(string Id,string Name,int Grade);

public record TimetableRawCell(string ClassKey,int Grade,int ClassNumber,int Day,int Period,string Subject,string Teacher,string Address,string? Date=null);
public record TimetableWorkbookData(string Kind,int Year,int Semester,string? Date,TimetableRawCell[] Cells);
public record TimetableLink(string Source,string Id);
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
public sealed record TimetableSwapOptionsRequest(string[] Dates, string[]? Dependencies, TimetableExchange[]? Exchanges, TimetablePosition Pick);
// One lesson the picked lesson can trade places with, and every exchange that trade needs (bundled classes move along).
public sealed record TimetableSwapOption(TimetablePosition Target, TimetableExchange[] Exchanges);
// Lessons that run at the same time for several classes: wherever these (class, subject) lessons share a period, they move together.
public sealed record TimetableBundleMember(string ClassId, string Subject);
public sealed record TimetableBundleRule(string Id, string Name, TimetableBundleMember[] Members);
public sealed record TimetableBaseLesson(string ClassId, string ClassName, int Grade, string Subject, string Teachers, int Hours);
public sealed record TimetableSwapCreate(string Id, long Revision, TimetableExchange[] Exchanges, string[]? Dependencies, string? Reason);
public sealed record TimetableSwapDecision(string Status);
public sealed record TimetablePlanSlot(TimetablePosition Position, TimetableSlot Lesson, bool Mine, string? BundleName = null);
public sealed record TimetableSwapPlan(long Revision, TimetablePlanSlot[] Slots);
public sealed record TimetableSwapChange(TimetablePosition Position, TimetableSlot Before, TimetableSlot After);
public sealed record TimetableSwap(
    string Id, string Owner, string OwnerName, string Status, string Reason, long CreatedAt,
    string[] Dependencies, TimetableExchange[] Exchanges, TimetableSwapChange[] Changes,
    string? Reviewer = null, string? ReviewerName = null, long? ReviewedAt = null, long? PublishedRevision = null);
public sealed record TimetableBundle(string Id, string Name, string Kind, TimetablePosition[] Slots);
