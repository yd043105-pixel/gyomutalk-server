using System.Text.Json.Serialization;

namespace SchoolMessenger.Contracts;

public sealed record FeedbackRequest(string ClientId,string Kind,string Product,string Area,string Title,string Details,string Version,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode=null);
public sealed record FeedbackReview(int Revision,string Status,string DevelopmentSummary,bool ApproveForDevelopment);
