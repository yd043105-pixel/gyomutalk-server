namespace SchoolMessenger.Contracts;

public record RegistrationDocument(string Id,string Title,string Version,string Body,bool Required,string Action);
public record RegistrationPolicySet(string Revision,bool CanRegister,bool Draft,string Message,string Operator,string Contact,RegistrationDocument[] Documents);
public record RegistrationAcceptance(string? Revision,string[]? AcceptedIds);
