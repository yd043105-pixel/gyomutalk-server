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
    // Group rooms show the first two other members as overlapping avatars.
    string First => Members.Where(p => p.Id != SelfId).Select(p => p.Name).FirstOrDefault() ?? Display;
    string Second => Members.Where(p => p.Id != SelfId).Select(p => p.Name).Skip(1).FirstOrDefault() ?? "";
    [System.Text.Json.Serialization.JsonIgnore] public string Initial => Avatar.Initial(Direct ? First : Alias.Length > 0 || Second.Length == 0 ? Display : First);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush AvatarBack => Avatar.Back(Direct ? First : Display);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush AvatarInk => Avatar.Ink(Direct ? First : Display);
    [System.Text.Json.Serialization.JsonIgnore] public bool Group => !Direct && Second.Length > 0;
    [System.Text.Json.Serialization.JsonIgnore] public string SecondInitial => Avatar.Initial(Second.Length > 0 ? Second : Display);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush SecondBack => Avatar.Back(Second);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush SecondInk => Avatar.Ink(Second);
}
public record ChatEntry(long Sequence, string Id, string SenderId, string SenderName, string Body, long CreatedAt, Attachment[] Attachments)
{
    public string Heading { get { var at = DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt).ToLocalTime(); return (Mine ? "나" : SenderName) + " · " + at.ToString(at.Date == DateTime.Today ? "HH:mm" : "M.d HH:mm"); } }
    // The signed-in teacher; their own messages sit on the right like in other messengers.
    public static string? Me { get; set; }
    public bool Mine => SenderId == Me;
    [System.Text.Json.Serialization.JsonIgnore] public string Initial => Avatar.Initial(SenderName);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush AvatarBack => Avatar.Back(SenderName);
    [System.Text.Json.Serialization.JsonIgnore] public System.Windows.Media.Brush AvatarInk => Avatar.Ink(SenderName);
}
public record Suggestion(string Id, string MessageId, string Title, string Evidence, string? DueDate, long CreatedAt);
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
