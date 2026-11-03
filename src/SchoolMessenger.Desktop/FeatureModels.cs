namespace SchoolMessenger.Desktop;

public record ChatRoom(string Id, string Name, bool Direct, int Unread, Person[] Members, long LastAt = 0, string Alias = "", string Preview = "", long LastRead = 0)
{
    public string SelfId { get; init; } = "";
    public bool Muted { get; init; }
    public string Display => Alias.Length > 0 ? Alias : Direct ? string.Join(", ", Members.Where(p => p.Id != SelfId).Select(p => p.Name)) : Name;
    public string Badge => Unread > 99 ? "99+" : Unread.ToString();
    public string When
    {
        get
        {
            if (LastAt == 0) return "";
            var at = DateTimeOffset.FromUnixTimeMilliseconds(LastAt).ToLocalTime();
            var days = (DateTime.Today - at.Date).Days;
            return days == 0 ? at.ToString("HH:mm") : days == 1 ? "어제" : at.ToString("M/d");
        }
    }
    public string Participants => string.Join(", ", Members.Select(p => p.Name));
}
public record ChatEntry(long Sequence, string Id, string SenderId, string SenderName, string Body, long CreatedAt, Attachment[] Attachments)
{
    public string Heading { get { var at = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime(); return (Mine ? "나" : SenderName) + " · " + at.ToString(at.Date == DateTime.Today ? "HH:mm" : "M.d HH:mm"); } }
    // The signed-in teacher; their own messages sit on the right like in other messengers.
    public static string? Me { get; set; }
    public bool Mine => SenderId == Me;
}
public record ChatPending(string Server, string RoomId, string ClientId, string Body, string[] AttachmentIds);
public record SurveyQuestion(string Text, string Kind, bool Required, string[] Options);
public record SurveyItem(string Id, string OwnerId, string Title, string OwnerName, long Deadline, bool Closed, int TargetCount, int ResponseCount, bool Answered, bool Anonymous = false, bool Target = false)
{
    public string Label => Title;
    public bool Sent { get; init; }
    public string Status => Sent ? $"{(Closed ? "종료" : "진행 중")} · 응답 {ResponseCount}/{TargetCount}명" : $"{OwnerName} · {(Closed ? "종료" : Answered ? "응답 완료" : "미응답")} · {ResponseCount}/{TargetCount}명";
}
public record SurveyDetail(string Id, string OwnerId, string Title, string Description, SurveyQuestion[] Questions, long Deadline, bool Closed, bool CanAnswer, string[]? Answers, bool Anonymous = false, bool Answered = false);
public record SurveyResponse(string Id, string Name, string Department, string[] Answers, long UpdatedAt);
public record SurveyResults(SurveyQuestion[] Questions, SurveyResponse[] Responses, bool Anonymous = false);
public record RemoteOffer(string Id, string HostId, string HostName, string ViewerId, string ViewerName, string InitiatorName, bool Control, string State, long ExpiresAt, int ControlRevision = 0);
public record RemotePeer(string Id, string HostAddress, int HostPort, string HostPin, string ViewerPin);
public record PeerInput(int Revision, RemoteInput Input);
public record RemoteInput(string Kind, double X = 0, double Y = 0, int Value = 0, bool Down = false, string? Text = null);
