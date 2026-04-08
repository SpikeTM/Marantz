namespace MarantzDesktopControl.Models;

public sealed class NetAudioMenu
{
    public string NetFunction { get; init; } = string.Empty;
    public string PlaybackStatus { get; init; } = string.Empty;
    public IReadOnlyList<string> MenuLines { get; init; } = [];
    public int CursorLine { get; init; }
    public int TotalLines { get; init; }
    public int ListLayer { get; init; }
}
