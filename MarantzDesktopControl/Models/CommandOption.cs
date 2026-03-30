namespace MarantzDesktopControl.Models;

public sealed class CommandOption
{
    public string Code { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? Code : Label;
}
