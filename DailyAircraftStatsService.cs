using System.Text.Json;
using Discord;
using Discord.WebSocket;
using System.Collections.Concurrent;

public class DailyAircraftStatsService
{
    private readonly DiscordSocketClient _discordClient;

    // Discord channel where messages go
    private readonly ulong _statsChannelId = 12345678910;

    // CSVs
    private readonly string _mainCsvPath = "path to your local csv and name";
    private readonly string _backupCsvPath = "path to backup and name";

    // dump1090 JSON source
    private readonly string _adsbJsonUrl = "http://192.168.x.xxx:8080/data/aircraft.json";

    // Lookup DBs
    private readonly Dictionary<string, AircraftInfo> _mainRegistry = new();
    private readonly Dictionary<string, AircraftInfo> _backupRegistry = new();

    // State in memory (no DB, easier for Pi/SD card setups)
    private readonly ConcurrentDictionary<string, SeenAircraft> _aircraftSeen = new();
    private readonly HashSet<string> _uniqueToday = new();
    private readonly List<ulong> _messageHistory = new();

    private readonly HttpClient _httpClient = new();
    private DateTime _utcDayTracker = DateTime.UtcNow.Date;

    public DailyAircraftStatsService(DiscordSocketClient client)
    {
        _discordClient = client;
    }

    public void Start()
    {
        LoadMainCsv();
        LoadBackupCsv();

        // Background loop
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await PollLoop();
                }
                catch (Exception ex)
                {
                    // Not ideal error handling but works for now
                    Console.WriteLine($"[ERROR] {ex.Message}");
                }

                // Changed interval from 1 to 3 mins — feels safer for rate-limits
                await Task.Delay(TimeSpan.FromMinutes(3));
            }
        });
    }

    private void LoadMainCsv()
    {
        foreach (var line in File.ReadLines(_mainCsvPath).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 31) continue;

            var hex = parts[0].Trim().Trim('\'').ToLowerInvariant();
            var reg = parts[27].Trim('\'');
            var type = parts[30].Trim('\'');
            var op = parts[18].Trim('\'');

            if (!_mainRegistry.ContainsKey(hex))
            {
                _mainRegistry[hex] = new AircraftInfo
                {
                    Registration = reg,
                    Type = string.IsNullOrWhiteSpace(type) ? "Unknown" : type,
                    Operator = op
                };
            }
        }

        Console.WriteLine($"[INFO] Main registry loaded: {_mainRegistry.Count} aircraft");
    }

    private void LoadBackupCsv()
    {
        if (!File.Exists(_backupCsvPath))
        {
            Console.WriteLine("[WARN] Backup CSV missing, skipping...");
            return;
        }

        foreach (var line in File.ReadLines(_backupCsvPath).Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 4) continue;

            var hex = cols[0].Trim().ToLowerInvariant();
            var reg = cols[1].Trim();
            var type = cols[2].Trim();
            var op = cols[3].Trim();

            _backupRegistry[hex] = new AircraftInfo
            {
                Registration = reg,
                Type = string.IsNullOrWhiteSpace(type) ? "Unknown" : type,
                Operator = op
            };
        }

        Console.WriteLine($"[INFO] Backup registry loaded: {_backupRegistry.Count} overrides");
    }

    private async Task PollLoop()
    {
        var nowUtc = DateTime.UtcNow;

        // Reset if new UTC day
        if (nowUtc.Date > _utcDayTracker)
        {
            _aircraftSeen.Clear();
            _uniqueToday.Clear();
            _messageHistory.Clear();
            _utcDayTracker = nowUtc.Date;

            Console.WriteLine($"[RESET] Counters cleared for {_utcDayTracker:yyyy-MM-dd}");
        }

        var json = await _httpClient.GetStringAsync(_adsbJsonUrl);
        using var doc = JsonDocument.Parse(json);
        var planes = doc.RootElement.GetProperty("aircraft");

        foreach (var plane in planes.EnumerateArray())
        {
            if (!plane.TryGetProperty("hex", out var hexNode)) continue;
            var hex = hexNode.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(hex)) continue;

            string flightId = plane.TryGetProperty("flight", out var fNode) ? fNode.GetString()?.Trim() ?? "" : "";

            // Default values
            string reg = "";
            string type = "Unknown";
            string op = "";

            bool usedBackup = false;

            if (_backupRegistry.TryGetValue(hex, out var fbInfo))
            {
                reg = fbInfo.Registration;
                type = fbInfo.Type;
                op = fbInfo.Operator;
                usedBackup = true;
            }
            else if (_mainRegistry.TryGetValue(hex, out var mainInfo))
            {
                reg = mainInfo.Registration;
                type = mainInfo.Type;
                op = mainInfo.Operator;
            }

            // Pick display name: prefer callsign > registration > hex
            string display;
            if (!string.IsNullOrWhiteSpace(flightId))
                display = flightId;
            else if (!string.IsNullOrWhiteSpace(reg))
                display = reg;
            else
                display = hex;

            if (_aircraftSeen.TryGetValue(hex, out var seen))
            {
                if (string.IsNullOrWhiteSpace(flightId))
                {
                    // Don’t overwrite if callsign disappears
                    Console.WriteLine($"[IGNORE] {seen.Type} {hex} lost callsign, keeping {seen.Callsign}");
                    continue;
                }

                if (seen.Callsign != display)
                {
                    seen.Callsign = display;
                    Console.WriteLine($"[UPDATE] {type} — {display} ({reg}) [hex {hex}]");
                }
            }
            else
            {
                _aircraftSeen[hex] = new SeenAircraft
                {
                    Hex = hex,
                    Registration = string.IsNullOrWhiteSpace(reg) ? hex : reg,
                    Type = type,
                    Callsign = display
                };
                _uniqueToday.Add(hex);

                Console.WriteLine($"{(usedBackup ? "[BACKUP]" : "[NEW]")} {type} — {display} ({reg}) [hex {hex}]");
            }
        }

        await PushStatsToDiscord(nowUtc);
    }

    private async Task PushStatsToDiscord(DateTime nowUtc)
    {
        if (_discordClient.GetChannel(_statsChannelId) is not IMessageChannel channel) return;

        var grouped = _aircraftSeen.Values
            .GroupBy(a => a.Type)
            .OrderByDescending(g => g.Count())
            .ToList();

        string topType = grouped.Any() ? $"{grouped.First().Key} ({grouped.First().Count()})" : "None";

        int embedNum = 0;
        int fieldCount = 0;

        var embed = MakeBaseEmbed(nowUtc, topType);

        foreach (var grp in grouped)
        {
            var calls = string.Join("\n", grp.Select(a =>
                $"{a.Callsign} ({(string.IsNullOrWhiteSpace(a.Registration) ? a.Hex : a.Registration)})"));

            if (fieldCount >= 25)   // Discord limit
            {
                await SendOrUpdateMessage(channel, embed, embedNum++);
                embed = MakeBaseEmbed(nowUtc, topType);
                fieldCount = 0;
            }

            embed.AddField($"{grp.Key} ({grp.Count()})", calls);
            fieldCount++;
        }

        if (fieldCount > 0 || embed.Fields.Count > 0)
        {
            await SendOrUpdateMessage(channel, embed, embedNum);
        }
    }

    private EmbedBuilder MakeBaseEmbed(DateTime nowUtc, string topType)
    {
        return new EmbedBuilder()
            .WithTitle($"✈️ Aircraft Seen — {nowUtc:yyyy-MM-dd} UTC")
            .WithColor(Color.Blue)
            .WithTimestamp(nowUtc)
            .WithDescription(
                $"Unique aircraft today: {_uniqueToday.Count}\n" +
                $"Most common type: {topType}"
            );
    }

    private async Task SendOrUpdateMessage(IMessageChannel channel, EmbedBuilder embed, int index)
    {
        if (index < _messageHistory.Count)
        {
            var msgId = _messageHistory[index];
            var existing = await channel.GetMessageAsync(msgId) as IUserMessage;
            if (existing != null)
            {
                await existing.ModifyAsync(m => m.Embed = embed.Build());
                return;
            }
        }

        var newMsg = await channel.SendMessageAsync(embed: embed.Build());
        if (index < _messageHistory.Count)
            _messageHistory[index] = newMsg.Id;
        else
            _messageHistory.Add(newMsg.Id);
    }
}

public class AircraftInfo
{
    public string Registration { get; set; } = "";
    public string Type { get; set; } = "Unknown";
    public string Operator { get; set; } = "";
}

public class SeenAircraft
{
    public string Hex { get; set; } = "";
    public string Registration { get; set; } = "";
    public string Type { get; set; } = "Unknown";
    public string Callsign { get; set; } = "";
}
