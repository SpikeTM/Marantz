using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using MarantzDesktopControl.Models;

namespace MarantzDesktopControl.Services;

public sealed class MarantzReceiverClient : IDisposable
{
    private static readonly Regex ValueRegex = new("<(?<key>[A-Za-z0-9_]+)><value>(?<value>.*?)</value></\\k<key>>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NetLineRegex = new("<szLine>\\s*(?<block>.*?)\\s*</szLine>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineValueRegex = new("<value>(?<value>.*?)</value>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex IndexedValueRegex = new("<value\\s+index='(?<index>[^']+)'(?:\\s+table='(?<table>[^']*)')?.*?>(?<value>.*?)</value>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ListBlockRegex = new("<(?<name>[A-Za-z0-9_]+Lists)>\\s*(?<block>.*?)\\s*</\\k<name>>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex NetCommandRegex = new("<L\\dCol\\d_CMD><value>(?<cmd>[^<]+)</value></L\\dCol\\d_CMD>", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public MarantzReceiverClient(string receiverIp)
    {
        if (string.IsNullOrWhiteSpace(receiverIp))
        {
            throw new ArgumentException("Receiver IP is required.", nameof(receiverIp));
        }

        string baseAddress = receiverIp.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? receiverIp.TrimEnd('/') + "/"
            : $"http://{receiverIp.TrimEnd('/')}/";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromSeconds(6)
        };
    }

    public async Task<MainZoneStatus> GetMainZoneStatusAsync(CancellationToken cancellationToken = default)
        => await GetZoneStatusAsync("MAIN ZONE", cancellationToken);

    public async Task<MainZoneStatus> GetZoneStatusAsync(string zoneName, CancellationToken cancellationToken = default)
    {
        string xml = await _httpClient.GetStringAsync($"goform/formMainZone_MainZoneXml.xml?ZoneName={Uri.EscapeDataString(zoneName)}", cancellationToken);
        Dictionary<string, string> map = ParseValueMap(xml);

        return new MainZoneStatus
        {
            ZoneName = GetValue(map, "RenameZone", "MAIN ZONE"),
            ZonePower = GetValue(map, "ZonePower", "UNKNOWN"),
            InputSource = GetValue(map, "InputFuncSelect", "UNKNOWN"),
            Volume = GetValue(map, "MasterVolume", "--"),
            MuteState = GetValue(map, "Mute", "UNKNOWN")
        };
    }

    public async Task<NetAudioStatus> GetNetAudioStatusAsync(CancellationToken cancellationToken = default)
    {
        string xml = await _httpClient.GetStringAsync("goform/formNetAudio_StatusXml.xml?ZoneName=MAIN%20ZONE&Login=self", cancellationToken);
        string nowPlaying = ExtractNowPlaying(xml);

        return new NetAudioStatus
        {
            NowPlaying = nowPlaying
        };
    }

    public async Task SendMainZoneCommandAsync(string command, CancellationToken cancellationToken = default)
        => await SendZoneCommandAsync("MAIN ZONE", command, cancellationToken);

    public async Task SendZoneCommandAsync(string zoneName, string command, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> payload = BuildCommandPayload(command);
        payload["ZoneName"] = zoneName;
        using var content = new FormUrlEncodedContent(payload);
        using HttpResponseMessage response = await _httpClient.PostAsync("MainZone/index.put.asp", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SendMainZoneCommandsAsync(IEnumerable<string> commands, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> payload = BuildCommandPayload(commands.ToArray());
        using var content = new FormUrlEncodedContent(payload);
        using HttpResponseMessage response = await _httpClient.PostAsync("MainZone/index.put.asp", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<CommandOption>> GetDiscoveredSourcesAsync(string zoneName, CancellationToken cancellationToken = default)
    {
        string xml = await _httpClient.GetStringAsync($"goform/formMainZone_MainZoneXml.xml?ZoneName={Uri.EscapeDataString(zoneName)}", cancellationToken);
        List<CommandOption> result = new();

        foreach (Match listMatch in ListBlockRegex.Matches(xml))
        {
            string listName = listMatch.Groups["name"].Value;
            if (!string.Equals(listName, "VideoSelectLists", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(listName, "InputFuncList", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match valueMatch in IndexedValueRegex.Matches(listMatch.Groups["block"].Value))
            {
                string code = WebUtility.HtmlDecode(valueMatch.Groups["index"].Value).Trim();
                string label = WebUtility.HtmlDecode(valueMatch.Groups["value"].Value).Trim();

                if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "ON", StringComparison.OrdinalIgnoreCase) || string.Equals(code, "OFF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    label = code;
                }

                if (result.All(x => !string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new CommandOption { Code = code, Label = label });
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<CommandOption>> GetNetworkPresetOptionsAsync(CancellationToken cancellationToken = default)
    {
        string xml = await _httpClient.GetStringAsync("goform/formNetAudio_StatusXml.xml?ZoneName=MAIN%20ZONE&Login=self", cancellationToken);
        List<CommandOption> result = new();

        foreach (Match match in NetCommandRegex.Matches(xml))
        {
            string code = WebUtility.HtmlDecode(match.Groups["cmd"].Value).Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            string label = code switch
            {
                "IRADIO" => "Internet Radio",
                "SERVER" => "Media Server",
                "FAVORITES" => "Favorites",
                "PANDORA" => "Pandora",
                "SIRIUSXM" => "SiriusXM",
                _ => code
            };

            if (result.All(x => !string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new CommandOption { Code = code, Label = label });
            }
        }

        return result;
    }

    public async Task SendNetAudioCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> payload = BuildCommandPayload(command);
        payload["ZoneName"] = "MAIN ZONE";
        payload["Login"] = "self";

        using var content = new FormUrlEncodedContent(payload);
        using HttpResponseMessage response = await _httpClient.PostAsync("NetAudio/index.put.asp", content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static Dictionary<string, string> BuildCommandPayload(params string[] commands)
    {
        if (commands.Length == 0)
        {
            throw new ArgumentException("At least one command is required.", nameof(commands));
        }

        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        int index = 0;
        foreach (string command in commands.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            payload[$"cmd{index}"] = command;
            index++;
        }

        if (index == 0)
        {
            throw new ArgumentException("No valid commands were provided.", nameof(commands));
        }

        payload[$"cmd{index}"] = "aspMainZone_WebUpdateStatus/";
        return payload;
    }

    private static string ExtractNowPlaying(string xml)
    {
        Match lineBlock = NetLineRegex.Match(xml);
        if (!lineBlock.Success)
        {
            return string.Empty;
        }

        string block = lineBlock.Groups["block"].Value;
        List<string> values = LineValueRegex.Matches(block)
            .Select(m => WebUtility.HtmlDecode(m.Groups["value"].Value).Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" | ", values.Take(3));
    }

    private static Dictionary<string, string> ParseValueMap(string xml)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in ValueRegex.Matches(xml))
        {
            string key = match.Groups["key"].Value;
            string value = WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            map[key] = value;
        }

        return map;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> map, string key, string fallback)
    {
        return map.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
