using System.Net;

/// <summary>
/// A parsed GameSpy \status\ reply from a Refractor-engine server (BF1942 or Battlefield Vietnam).
/// </summary>
/// <remarks>
/// Both games speak the same protocol but differ in spelling, so keys are looked up through a list
/// of candidates, BF1942's name first: mapId/map_id, gameId/game_id, playername_N/player_N,
/// tickets1/tickets_t0, allied_team_ratio/us_team_ratio, time_limit/timelimit.
///
/// Servers also disagree within a single game. A live BF1942 CTF server was observed omitting
/// ticket_ratio outright, and BFV never sends gamename, roundTime, hit_indicator, tk_mode,
/// averageFPS, content_check or unpure_mods. Treating every key as mandatory therefore loses whole
/// servers from the browser over a field nobody needs, so only what the browser and the file sync
/// genuinely depend on is required -- host, map, player counts and the mod id. Everything else
/// falls back to a default. A key that is present but malformed still throws, since that is a real
/// protocol violation rather than a dialect difference.
/// </remarks>
public class Bf1942QueryResult
{
    public string GameName { get; init; }
    public string GameVersion { get; init; }
    public string GameId { get; init; }
    public uint HostPort { get; init; }
    public string HostName { get; init; }
    public string MapName { get; init; }
    public uint NumberOfPlayers { get; init; }
    public uint MaximumNumberOfPlayers { get; init; }
    public bool HasPassword { get; init; }
    public string Mod { get; init; }
    public string GameType { get; init; }
    public string GameMode { get; init; }

    public int Tickets1 { get; init; }
    public int Tickets2 { get; init; }

    /// <summary>
    /// In seconds
    /// </summary>
    public int RoundTime { get; init; }

    /// <summary>
    /// In seconds
    /// </summary>
    public int RoundTimeRemain { get; init; }

    public bool AutoBalanceTeams { get; init; }
    public bool UsesPunkbuster { get; init; }
    public bool HitIndicator { get; init; }
    public bool FreeCamera { get; init; }
    public bool ExternalView { get; init; }
    public bool AllowNoseCam { get; init; }
    public DedicatedServerType DedicatedServerType { get; init; }

    public int ReservedSlots { get; init; }
    public int NumberOfRounds { get; init; }
    public int NameTagDistance { get; init; }
    public int NameTagDistanceScope { get; init; }

    /// <summary>
    /// Total round time in minutes.
    /// Null means unlimited.
    /// </summary>
    public int? TimeLimit { get; init; }
    public int AlliedTeamRatio { get; init; }
    public int AxisTeamRatio { get; init; }
    public int BandwidthChokeLimit { get; init; }
    public int ContentCheck { get; init; }
    public int AverageFps { get; init; }
    public int Cpu { get; init; }
    public int Status { get; init; }

    public int TicketRatio { get; init; }
    public int SpawnDelay { get; init; }
    public int SpawnWaveTime { get; init; }
    public string TkMode { get; init; }
    public int KickBack { get; init; }
    public int KickBackOnSplash { get; init; }
    public int SoldierFriendlyFire { get; init; }
    public int SoldierFriendlyFireOnSplash { get; init; }
    public int VehicleFriendlyFire { get; init; }
    public int VehicleFriendlyFireOnSplash { get; init; }
    public int GameStartDelay { get; init; }
    public string ActiveMods { get; init; }
    public List<string> UnpureMods { get; init; }

    public List<Player> Players { get; init; }

    /// <summary>
    /// Windows server only
    /// </summary>
    public int? Location { get; init; }

    /// <summary>
    /// Windows server only
    /// </summary>
    public string? Language { get; init; }

    public Bf1942QueryResult(Dictionary<string, string> properties)
    {
        // Required: without these the server is not usable in the browser or for syncing.
        HostName = Str(properties, "hostname");
        HostPort = Uint(properties, "hostport");
        MapName = Str(properties, "mapname");
        NumberOfPlayers = Uint(properties, "numplayers");
        MaximumNumberOfPlayers = Uint(properties, "maxplayers");
        Mod = Str(properties, "mapId", "map_id");

        GameName = OptStr(properties, "", "gamename");
        GameVersion = OptStr(properties, "", "gamever");
        GameId = OptStr(properties, "", "gameId", "game_id");
        HasPassword = OptBool(properties, false, "password");
        GameType = OptStr(properties, "", "gametype");
        GameMode = OptStr(properties, "", "gamemode");

        Tickets1 = OptInt(properties, 0, "tickets1", "tickets_t0");
        Tickets2 = OptInt(properties, 0, "tickets2", "tickets_t1");

        RoundTime = OptInt(properties, 0, "roundTime");
        RoundTimeRemain = OptInt(properties, 0, "roundTimeRemain");

        AutoBalanceTeams = OptBool(properties, false, "auto_balance_teams");
        UsesPunkbuster = OptBool(properties, false, "sv_punkbuster");
        HitIndicator = OptBool(properties, false, "hit_indicator");
        FreeCamera = OptBool(properties, false, "free_camera");
        ExternalView = OptBool(properties, false, "external_view");
        AllowNoseCam = OptBool(properties, false, "allow_nose_cam");

        DedicatedServerType = (DedicatedServerType)OptInt(properties, 0, "dedicated");
        ReservedSlots = OptInt(properties, 0, "reservedslots");
        NumberOfRounds = OptInt(properties, 0, "number_of_rounds");
        NameTagDistance = OptInt(properties, 0, "name_tag_distance");
        NameTagDistanceScope = OptInt(properties, 0, "name_tag_distance_scope");
        TimeLimit = OptIntThatCanBeInfinite(properties, "time_limit", "timelimit");
        AlliedTeamRatio = OptInt(properties, 0, "allied_team_ratio", "us_team_ratio");
        AxisTeamRatio = OptInt(properties, 0, "axis_team_ratio", "nva_team_ratio");
        BandwidthChokeLimit = OptInt(properties, 0, "bandwidth_choke_limit");
        ContentCheck = OptInt(properties, 0, "content_check");
        AverageFps = OptInt(properties, 0, "averageFPS");
        Cpu = OptInt(properties, 0, "cpu");
        Status = OptInt(properties, 0, "status");

        TicketRatio = OptIntWithPostfix(properties, 0, '%', "ticket_ratio");
        SpawnDelay = OptIntWithPostfix(properties, 0, 's', "spawn_delay");
        SpawnWaveTime = OptIntWithPostfix(properties, 0, 's', "spawn_wave_time");
        TkMode = OptStr(properties, "", "tk_mode");
        KickBack = OptIntWithPostfix(properties, 0, '%', "kickback");
        KickBackOnSplash = OptIntWithPostfix(properties, 0, '%', "kickback_on_splash");
        SoldierFriendlyFire = OptIntWithPostfix(properties, 0, '%', "soldier_friendly_fire");
        SoldierFriendlyFireOnSplash = OptIntWithPostfix(properties, 0, '%', "soldier_friendly_fire_on_splash");
        VehicleFriendlyFire = OptIntWithPostfix(properties, 0, '%', "vehicle_friendly_fire");
        VehicleFriendlyFireOnSplash = OptIntWithPostfix(properties, 0, '%', "vehicle_friendly_fire_on_splash");
        GameStartDelay = OptIntWithPostfix(properties, 0, 's', "game_start_delay");
        ActiveMods = OptStr(properties, "", "active_mods");

        var unpureStr = OptStr(properties, "", "unpure_mods");
        UnpureMods = string.IsNullOrEmpty(unpureStr)
            ? []
            : [.. unpureStr.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)];

        // Servers can advertise more players than they send blocks for, so a name that never
        // arrived is skipped rather than taken as a protocol violation.
        Players = [];
        for (int i = 0; i < NumberOfPlayers; i++)
        {
            var name = OptStr(properties, "", "playername_" + i, "player_" + i);
            if (name.Length == 0)
                continue;

            Players.Add(new Player(
                Bf1942Encoding.Decode(Bf1942Encoding.Encode(name), applySmartEncodingDetection: true),
                OptStr(properties, "", "team_" + i),
                OptStr(properties, "", "score_" + i),
                OptStr(properties, "", "kills_" + i),
                OptStr(properties, "", "deaths_" + i),
                OptStr(properties, "", "ping_" + i),
                OptStr(properties, "", "keyhash_" + i)
            ));
        }

        Location = properties.ContainsKey("location") ? OptInt(properties, 0, "location") : null;
        Language = properties.ContainsKey("language") ? OptStr(properties, "", "language") : null;
    }

    /// <summary>
    /// The value of the first of <paramref name="keys"/> the server actually sent, or null.
    /// </summary>
    private static string? Find(Dictionary<string, string> p, string[] keys)
    {
        foreach (var key in keys)
            if (p.TryGetValue(key, out var value))
                return value;
        return null;
    }

    private static string Names(string[] keys) => string.Join('/', keys);

    private static string Str(Dictionary<string, string> p, params string[] keys)
        => Find(p, keys) ?? throw new ProtocolViolationException($"Missing required key: {Names(keys)}");

    private static uint Uint(Dictionary<string, string> p, params string[] keys)
    {
        var value = Str(p, keys);
        if (!uint.TryParse(value, out var result))
            throw new ProtocolViolationException($"Unexpected uint value for {Names(keys)}: {value}");
        return result;
    }

    private static string OptStr(Dictionary<string, string> p, string @default, params string[] keys)
        => Find(p, keys) ?? @default;

    private static int OptInt(Dictionary<string, string> p, int @default, params string[] keys)
    {
        var value = Find(p, keys);
        if (value == null)
            return @default;
        if (!int.TryParse(value, out var result))
            throw new ProtocolViolationException($"Unexpected int value for {Names(keys)}: {value}");
        return result;
    }

    private static int OptIntWithPostfix(Dictionary<string, string> p, int @default, char postfix, params string[] keys)
    {
        var value = Find(p, keys);
        if (value == null)
            return @default;
        if (!value.EndsWith(postfix) || !int.TryParse(value[..^1], out var result))
            throw new ProtocolViolationException($"Unexpected int (with postfix {postfix}) value for {Names(keys)}: {value}");
        return result;
    }

    private static int? OptIntThatCanBeInfinite(Dictionary<string, string> p, params string[] keys)
    {
        var value = Find(p, keys);
        if (value == null || value == "unlimited")
            return null;
        if (!int.TryParse(value, out var result))
            throw new ProtocolViolationException($"Unexpected int value for {Names(keys)}: {value}");
        return result;
    }

    private static bool OptBool(Dictionary<string, string> p, bool @default, params string[] keys)
    {
        var value = Find(p, keys);
        if (value == null)
            return @default;
        if (value is "on" or "yes" or "1")
            return true;
        if (value is "off" or "no" or "0")
            return false;
        throw new ProtocolViolationException($"Unexpected bool value for {Names(keys)}: {value}");
    }
}
