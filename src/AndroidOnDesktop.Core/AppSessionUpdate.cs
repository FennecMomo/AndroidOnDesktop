namespace AndroidOnDesktop.Core;

public enum AppSessionState
{
    Starting,
    Running,
    Failed,
    Closed
}

public sealed record AppSessionUpdate(
    Guid SessionId,
    string PackageName,
    string DeviceSerial,
    AppSessionState State,
    string Message,
    string? TechnicalDetail = null);
