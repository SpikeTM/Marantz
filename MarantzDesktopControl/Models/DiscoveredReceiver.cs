namespace MarantzDesktopControl.Models;

public sealed class DiscoveredReceiver
{
    public string FriendlyName { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string Manufacturer { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;

    public string DisplayName
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(FriendlyName) ? "Marantz Receiver" : FriendlyName;
            string model = string.IsNullOrWhiteSpace(ModelName) ? string.Empty : $" ({ModelName})";
            return $"{name}{model} - {IpAddress}";
        }
    }
}
