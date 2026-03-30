using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MarantzDesktopControl.Models;

namespace MarantzDesktopControl.Services;

public sealed class SsdpDiscoveryService
{
    private static readonly Regex HeaderRegex = new("^(?<k>[^:]+):\\s*(?<v>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(750) };

    public async Task<IReadOnlyList<DiscoveredReceiver>> DiscoverMarantzReceiversAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        var discovered = new Dictionary<string, DiscoveredReceiver>(StringComparer.OrdinalIgnoreCase);
        var discoveredLock = new object();

        Task ssdpTask = DiscoverViaSsdpAsync(discovered, discoveredLock, deadline, cancellationToken);
        Task subnetTask = DiscoverViaSubnetProbeAsync(discovered, discoveredLock, deadline, cancellationToken);
        await Task.WhenAll(ssdpTask, subnetTask);

        return discovered.Values.OrderBy(x => x.FriendlyName).ThenBy(x => x.IpAddress).ToList();
    }

    private async Task DiscoverViaSsdpAsync(Dictionary<string, DiscoveredReceiver> discovered, object discoveredLock, DateTime deadline, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;

        string request = string.Join("\r\n", new[]
        {
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 2",
            "ST: ssdp:all",
            "",
            ""
        });

        byte[] payload = Encoding.ASCII.GetBytes(request);
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await udp.SendAsync(payload, payload.Length, multicastEndpoint);

        while (DateTime.UtcNow < deadline)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync();
            Task delayTask = Task.Delay(remaining, cancellationToken);
            Task winner = await Task.WhenAny(receiveTask, delayTask);

            if (winner != receiveTask)
            {
                break;
            }

            UdpReceiveResult packet;
            try
            {
                packet = await receiveTask;
            }
            catch
            {
                continue;
            }

            string text = Encoding.UTF8.GetString(packet.Buffer);
            Dictionary<string, string> headers = ParseHeaders(text);

            if (!headers.TryGetValue("LOCATION", out string? location) || string.IsNullOrWhiteSpace(location))
            {
                continue;
            }

            DiscoveredReceiver? receiver = await TryResolveReceiverFromLocationAsync(location, cancellationToken);
            if (receiver is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(receiver.IpAddress))
            {
                lock (discoveredLock)
                {
                    discovered[receiver.IpAddress] = receiver;
                }
            }
        }
    }

    private async Task DiscoverViaSubnetProbeAsync(Dictionary<string, DiscoveredReceiver> discovered, object discoveredLock, DateTime deadline, CancellationToken cancellationToken)
    {
        List<string> candidates = GetLocalSubnetCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        using var throttle = new SemaphoreSlim(36);
        var results = new ConcurrentDictionary<string, DiscoveredReceiver>(StringComparer.OrdinalIgnoreCase);
        var probeTasks = new List<Task>();

        foreach (string ip in candidates)
        {
            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            await throttle.WaitAsync(cancellationToken);
            probeTasks.Add(Task.Run(async () =>
            {
                try
                {
                    DiscoveredReceiver? receiver = await TryProbeReceiverByIpAsync(ip, deadline, cancellationToken);
                    if (receiver is not null)
                    {
                        results[ip] = receiver;
                    }
                }
                catch
                {
                    // Ignore failed probes.
                }
                finally
                {
                    throttle.Release();
                }
            }, cancellationToken));
        }

        while (probeTasks.Count > 0 && DateTime.UtcNow < deadline)
        {
            Task completed = await Task.WhenAny(probeTasks);
            probeTasks.Remove(completed);
        }

        foreach (var pair in results)
        {
            lock (discoveredLock)
            {
                discovered[pair.Key] = pair.Value;
            }
        }
    }

    private static List<string> GetLocalSubnetCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (UnicastIPAddressInformation uni in properties.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                string[] octets = uni.Address.ToString().Split('.');
                if (octets.Length != 4)
                {
                    continue;
                }

                if (octets[0] == "169" && octets[1] == "254")
                {
                    continue;
                }

                string prefix = $"{octets[0]}.{octets[1]}.{octets[2]}";
                for (int host = 1; host <= 254; host++)
                {
                    string candidate = $"{prefix}.{host}";
                    if (candidate != uni.Address.ToString())
                    {
                        candidates.Add(candidate);
                    }
                }
            }
        }

        return candidates.OrderBy(ip => ip).ToList();
    }

    private async Task<DiscoveredReceiver?> TryProbeReceiverByIpAsync(string ipAddress, DateTime deadline, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow >= deadline)
        {
            return null;
        }

        // First, try common UPnP description endpoints.
        string[] descriptionUrls =
        [
            $"http://{ipAddress}/description.xml",
            $"http://{ipAddress}:8080/description.xml"
        ];

        foreach (string location in descriptionUrls)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            DiscoveredReceiver? fromDescription = await TryResolveReceiverFromLocationAsync(location, cancellationToken);
            if (fromDescription is not null)
            {
                return fromDescription;
            }
        }

        // Fallback: probe the Marantz HTTP XML endpoint used by the app.
        string zoneStatusUrl = $"http://{ipAddress}/goform/formMainZone_MainZoneXml.xml";
        string body;
        try
        {
            body = await Http.GetStringAsync(zoneStatusUrl, cancellationToken);
        }
        catch
        {
            return null;
        }

        string lowered = body.ToLowerInvariant();
        if (!lowered.Contains("<item>") || (!lowered.Contains("mastervolume") && !lowered.Contains("inputfuncselect")))
        {
            return null;
        }

        return new DiscoveredReceiver
        {
            FriendlyName = $"Marantz/Denon Receiver ({ipAddress})",
            Manufacturer = "Marantz/Denon",
            ModelName = string.Empty,
            IpAddress = ipAddress,
            Location = zoneStatusUrl
        };
    }

    private static Dictionary<string, string> ParseHeaders(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HeaderRegex.Matches(response))
        {
            string key = match.Groups["k"].Value.Trim();
            string value = match.Groups["v"].Value.Trim();
            headers[key] = value;
        }

        return headers;
    }

    private static async Task<DiscoveredReceiver?> TryResolveReceiverFromLocationAsync(string location, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        string description;
        try
        {
            description = await Http.GetStringAsync(uri, cancellationToken);
        }
        catch
        {
            return null;
        }

        string friendly = ExtractXmlValue(description, "friendlyName");
        string manufacturer = ExtractXmlValue(description, "manufacturer");
        string model = ExtractXmlValue(description, "modelName");

        string haystack = string.Join(" ", new[] { friendly, manufacturer, model }).ToLowerInvariant();
        if (!haystack.Contains("marantz") && !haystack.Contains("denon"))
        {
            return null;
        }

        return new DiscoveredReceiver
        {
            FriendlyName = friendly,
            Manufacturer = manufacturer,
            ModelName = model,
            IpAddress = uri.Host,
            Location = location
        };
    }

    private static string ExtractXmlValue(string xml, string tag)
    {
        Match match = Regex.Match(xml, $"<{tag}>(?<v>.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : string.Empty;
    }
}
