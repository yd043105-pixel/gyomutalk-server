namespace SchoolMessenger.Contracts;

public record TimetableRawCell(string ClassKey,int Grade,int ClassNumber,int Day,int Period,string Subject,string Teacher,string Address);
public record TimetableWorkbookData(string Kind,int Year,int Semester,string? Date,TimetableRawCell[] Cells);
public record TimetableLink(string Source,string Id);
public record TimetablePreviewRequest(string DraftId,string Start,string End,TimetableLink[] Classes,TimetableLink[] Teachers);
public record TimetableCommit(string CandidateId,string ClientId);
public record TimetableCell(string ClassId,string ClassName,int Day,int Period,string Subject,string TeacherId,string TeacherName);
public record TimetableBatch(string Id,long Revision,string Owner,string ClientId,string Kind,int Year,int Semester,string Start,string End,string? Date,string FileName,long PublishedAt,bool Withdrawn,TimetableCell[] Cells);
public record TimetableSlot(string ClassId,string ClassName,int Period,string Subject,string TeacherId,string TeacherName,bool Changed,string BeforeSubject,string BeforeTeacher,string Source);
public record TimetableDay(string Date,long Revision,long PublishedAt,string[] MissingClasses,TimetableSlot[] Slots);
public record TimetableNotice(long Revision,string UserId,string Date,string[] ClassIds,string Title,long CreatedAt,long? ReadAt=null);
public record TimetableHomeroom(string ClassId,string TeacherId);
public record TimetableDelivery(TimetableBatch Batch,TimetableHomeroom[] Homerooms,TimetableNotice[] Notices);
