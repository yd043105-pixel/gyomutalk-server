namespace SchoolMessenger.Desktop;

public record ChatRoom(string Id, string Name, bool Direct, int Unread, Person[] Members)
{
    public string SelfId { get; init; } = "";
    public string Display => Direct ? string.Join(", ", Members.Where(p => p.Id != SelfId).Select(p => p.Name)) : Name;
    public string Label => Display + (Unread > 0 ? $"  ({Unread})" : "");
    public string Participants => string.Join(", ", Members.Select(p => p.Name));
}
public record ChatEntry(long Sequence, string Id, string SenderId, string SenderName, string Body, long CreatedAt, Attachment[] Attachments)
{
    public string Heading => $"{SenderName} · {DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime():MM.dd HH:mm}";
}
public record ChatPending(string Server, string RoomId, string ClientId, string Body, string[] AttachmentIds);
public record SurveyQuestion(string Text, string Kind, bool Required, string[] Options);
public record SurveyItem(string Id, string OwnerId, string Title, string OwnerName, long Deadline, bool Closed, int TargetCount, int ResponseCount, bool Answered)
{
    public string Label => Title;
    public string Status => $"{OwnerName} · {(Closed ? "종료" : Answered ? "응답 완료" : "미응답")} · {ResponseCount}/{TargetCount}명";
}
public record SurveyDetail(string Id, string OwnerId, string Title, string Description, SurveyQuestion[] Questions, long Deadline, bool Closed, bool CanAnswer, string[]? Answers);
public record SurveyResponse(string Id, string Name, string Department, string[] Answers, long UpdatedAt);
public record SurveyResults(SurveyQuestion[] Questions, SurveyResponse[] Responses);
public record RemoteOffer(string Id, string HostId, string HostName, string ViewerId, string ViewerName, string InitiatorName, bool Control, string State, long ExpiresAt, int ControlRevision = 0);
public record RemotePeer(string Id, string HostAddress, int HostPort, string HostPin, string ViewerPin);
public record PeerInput(int Revision, RemoteInput Input);
public record RemoteInput(string Kind, double X = 0, double Y = 0, int Value = 0, bool Down = false, string? Text = null);
