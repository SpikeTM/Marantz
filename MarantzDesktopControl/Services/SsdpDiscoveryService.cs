using System.Collections.Concurrent;
using System.Diagnostics;
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
    /// <summary>
    /// IEEE OUI prefixes for Marantz Japan Inc. (00-06-78) and Denon, Ltd. (00-05-CD).
    /// </summary>
    private static readonly string[] KnownOuiPrefixes = ["00-06-78", "00-05-CD"];

    private static readonly Regex HeaderRegex = new(
        "^(?<k>[^:]+):\\s*(?<v>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ArpEntryRegex = new(
        @"^\s*(?<ip>\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\s+(?<mac>[0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+\w+",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<IReadOnlyList<DiscoveredReceiver>> DiscoverMarantzReceiversAsync(
        TimeSpan timeout,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        var discovered = new ConcurrentDictionary<string, DiscoveredReceiver>(StringComparer.OrdinalIgnoreCase);

        // Phase 1 (instant): check existing ARP cache for known Marantz/Denon MAC prefixes.
        progress?.Report("Checking ARP cache for Marantz devices...");
        List<string> cachedIps = GetMarantzIpsFromArpCache();
        if (cachedIps.Count > 0)
        {
            progress?.Report($"Found {cachedIps.Count} Marantz MAC(s) in ARP cache, verifying...");
            await ProbeIpListAsync(cachedIps, discovered, deadline, cancellationToken);
        }

        // Early exit if we already found receivers and less than 2 seconds remain.
        if (!discovered.IsEmpty && DateTime.UtcNow.Add(TimeSpan.FromSeconds(2)) >= deadline)
        {
            progress?.Report($"Scan complete — found {discovered.Count} receiver(s).");
            return ToSortedList(discovered);
        }

        // Phase 2 (parallel): SSDP multicast + ping sweep → ARP re-check.
        progress?.Report("Scanning network (SSDP + ARP sweep)...");
        Task ssdpTask = DiscoverViaSsdpAsync(discovered, deadline, cancellationToken);
        Task sweepTask = PingSweepThenProbeArpAsync(discovered, deadline, progress, cancellationToken);
        await Task.WhenAll(ssdpTask, sweepTask);

        // Phase 3 (fallback): brute-force HTTP probe when nothing else worked.
        if (discovered.IsEmpty && DateTime.UtcNow < deadline)
        {
            progress?.Report("Trying direct HTTP probes on subnet...");
            await DiscoverViaSubnetProbeAsync(discovered, deadline, cancellationToken);
        }

        progress?.Report(discovered.IsEmpty
            ? "Scan complete — no receivers found."
            : $"Scan complete — found {discovered.Count} receiver(s).");

        return ToSortedList(discovered);
    }

    private static IReadOnlyList<DiscoveredReceiver> ToSortedList(
        ConcurrentDictionary<string, DiscoveredReceiver> discovered) =>
        discovered.Values.OrderBy(x => x.FriendlyName).ThenBy(x => x.IpAddress).ToList();

    // ──────────────────────────────────────────────────────────────────
    //  ARP-based discovery (MAC address lookup)
    // ──────────────────────────────────────────────────────────────────

    private static List<string> GetMarantzIpsFromArpCache()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("arp", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return [];
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return ParseArpOutputForMarantz(output);
        }
        catch
        {
            return [];
        }
    }

    private static List<string> ParseArpOutputForMarantz(string arpOutput)
    {
        var results = new List<string>();
        foreach (Match match in ArpEntryRegex.Matches(arpOutput))
        {
            string mac = match.Groups["mac"].Value;
            if (KnownOuiPrefixes.Any(prefix => mac.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(match.Groups["ip"].Value);
            }
        }

        return results;
    }

    private async Task PingSweepThenProbeArpAsync(
        ConcurrentDictionary<string, DiscoveredReceiver> discovered,
        DateTime deadline,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        List<string> candidates = GetLocalSubnetCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        // Ping sweep to populate the OS ARP cache.
        using var throttle = new SemaphoreSlim(80);
        var tasks = new List<Task>();

        foreach (string ip in candidates)
        {
            if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await throttle.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    await ping.SendPingAsync(ip, 500);
                }
                catch
                {
                    // Ignore — we only care about populating the ARP table.
                }
                finally
                {
                    throttle.Release();
                }
            }, cancellationToken));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Ignore aggregate exceptions from cancelled/faulted tasks.
        }

        if (DateTime.UtcNow >= deadline)
        {
            return;
        }

        // Re-read ARP cache after the sweep.
        List<string> marantzIps = GetMarantzIpsFromArpCache();
        marantzIps.RemoveAll(ip => discovered.ContainsKey(ip));

        if (marantzIps.Count > 0)
        {
            progress?.Report($"Found {marantzIps.Count} new Marantz MAC(s) after sweep, verifying...");
            await ProbeIpListAsync(marantzIps, discovered, deadline, cancellationToken);
        }
    }

    private async Task ProbeIpListAsync(
        List<string> ipAddresses,
        ConcurrentDictionary<string, DiscoveredReceiver> discovered,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        var tasks = ipAddresses.Select(async ip =>
        {
            DiscoveredReceiver? receiver = await TryProbeReceiverByIpAsync(ip, deadline, cancellationToken);
            if (receiver is not null)
            {
                discovered[ip] = receiver;
            }
        });

        await Task.WhenAll(tasks);
    }

    // ──────────────────────────────────────────────────────────────────
    //  SSDP multicast discovery
    // ──────────────────────────────────────────────────────────────────

    private async Task DiscoverViaSsdpAsync(
        ConcurrentDictionary<string, DiscoveredReceiver> discovered,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.EnableBroadcast = true;

        string request = string.Join("\r\n",
        [
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 2",
            "ST: ssdp:all",
            "",
            ""
        ]);

        byte[] payload = Encoding.ASCII.GetBytes(request);
        var multicastEndpoint = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

        for (int i = 0; i < 3; i++)
        {
            await udp.SendAsync(payload, payload.Length, multicastEndpoint);
            if (i < 2)
            {
                await Task.Delay(150, cancellationToken);
            }
        }

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
            if (receiver is not null && !string.IsNullOrWhiteSpace(receiver.IpAddress))
            {
                discovered[receiver.IpAddress] = receiver;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Subnet brute-force HTTP probe (fallback)
    // ──────────────────────────────────────────────────────────────────

    private async Task DiscoverViaSubnetProbeAsync(
        ConcurrentDictionary<string, DiscoveredReceiver> discovered,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        List<string> candidates = GetLocalSubnetCandidates();
        if (candidates.Count == 0)
        {
            return;
        }

        using var throttle = new SemaphoreSlim(50);
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
                        discovered[ip] = receiver;
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
    }

    // ──────────────────────────────────────────────────────────────────
    //  Individual IP probe
    // ──────────────────────────────────────────────────────────────────

    private async Task<DiscoveredReceiver?> TryProbeReceiverByIpAsync(
        string ipAddress, DateTime deadline, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow >= deadline)
        {
            return null;
        }

        // Try the Marantz/Denon HTTP control endpoint first — fastest and most reliable.
        string zoneStatusUrl = $"http://{ipAddress}/goform/formMainZone_MainZoneXml.xml";
        try
        {
            string body = await Http.GetStringAsync(zoneStatusUrl, cancellationToken);
            string lowered = body.ToLowerInvariant();
            if (lowered.Contains("<item>") && (lowered.Contains("mastervolume") || lowered.Contains("inputfuncselect")))
            {
                string friendlyName = ExtractGoformValue(body, "FriendlyName");
                if (string.IsNullOrWhiteSpace(friendlyName))
                {
                    friendlyName = "Marantz/Denon Receiver";
                }

                string zoneName = ExtractGoformValue(body, "RenameZone");
                string modelName = !string.IsNullOrWhiteSpace(zoneName)
                    && !zoneName.Equals("MAIN ZONE", StringComparison.OrdinalIgnoreCase)
                    ? zoneName.Trim()
                    : string.Empty;

                return new DiscoveredReceiver
                {
                    FriendlyName = friendlyName,
                    Manufacturer = "Marantz/Denon",
                    ModelName = modelName,
                    IpAddress = ipAddress,
                    Location = zoneStatusUrl
                };
            }
        }
        catch
        {
            // Not a Marantz/Denon receiver at this IP, fall through to UPnP check.
        }

        // Fallback: try common UPnP description endpoints.
        string[] descriptionUrls =
        [
            $"http://{ipAddress}:60006/upnp/desc/aios_device/aios_device.xml",
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

        return null;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────────────────────────

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

    private static async Task<DiscoveredReceiver?> TryResolveReceiverFromLocationAsync(
        string location, CancellationToken cancellationToken)
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

    private static string ExtractGoformValue(string xml, string tag)
    {
        Match match = Regex.Match(xml,
            $@"<{tag}>\s*<value>(?<v>.*?)</value>\s*</{tag}>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : string.Empty;
    }

    private static string ExtractXmlValue(string xml, string tag)
    {
        Match match = Regex.Match(xml, $"<{tag}>(?<v>.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value).Trim() : string.Empty;
    }
}
