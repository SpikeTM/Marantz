namespace MarantzDesktopControl.Models;

public sealed class MainZoneStatus
{
    public string ZoneName { get; init; } = "MAIN ZONE";
    public string ZonePower { get; init; } = "UNKNOWN";
    public string InputSource { get; init; } = "UNKNOWN";
    public string Volume { get; init; } = "--";
    public string MuteState { get; init; } = "UNKNOWN";
}
