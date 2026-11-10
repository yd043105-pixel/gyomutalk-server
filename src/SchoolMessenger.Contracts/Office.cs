namespace SchoolMessenger.Contracts;

public record StaffAccountRequest(string LoginName, string Name, string Department, string? SeatIp, string TemporaryPassword,string Position="",bool UseSharedPassword=false);
public record StaffSeatRequest(string? SeatIp);
// Code is what earlier programs still send; the first sign-in no longer asks for a number.
public record StaffActivation(string NewPassword, RegistrationAcceptance? Agreement = null, string? Code = null);
public record OfficePermissionRequest(string UserId, bool CalendarManager, bool RoomManager);
public record OfficeEventRequest(string ClientId, string Title, string Description, string Start, string End, bool AllDay, int Revision = 0);
public record OfficeRoomRequest(string Name, string Location, bool Active = true, int Revision = 0);
public record OfficeBookingRequest(string ClientId, string RoomId, string Title, string Start, string End, int Revision = 0,int Grade=1);
public record OfficeRevision(int Revision);
