using System.Net;
using System.Text;

namespace DataField42.Core.Tests;

/// <summary>
/// Parsing of real \status\ replies captured from live BF1942 and Battlefield Vietnam servers.
/// </summary>
/// <remarks>
/// The fixtures in TestData are verbatim captures, concatenated in arrival order. They are the
/// record of what the two games actually send, which is the whole basis for the dual-key lookups
/// in Bf1942QueryResult -- so if a rename ever breaks one dialect, these fail rather than the
/// server silently vanishing from the browser.
/// </remarks>
public class QueryResultTests
{
    /// <summary>
    /// Fixtures hold one char per wire byte, so they are read back as Latin-1 to keep the byte
    /// values the game's own encoding table expects.
    /// </summary>
    private static Dictionary<string, string> Properties(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fixture);
        return GameSpyStatus.Parse(File.ReadAllText(path, Encoding.Latin1));
    }

    private static Bf1942QueryResult Parse(string fixture) => new(Properties(fixture));

    [Fact]
    public void Bf1942_reply_parses()
    {
        var r = Parse("bf1942_status.txt");

        Assert.Equal("*NEW* SiMPLE | BF1942", r.HostName);
        Assert.Equal(14567u, r.HostPort);
        Assert.Equal("el alamein", r.MapName);
        Assert.Equal("bfield1942", r.GameName);
        Assert.Equal("bf1942", r.GameId);
        Assert.Equal("BF1942", r.Mod);
        Assert.Equal("v1.61", r.GameVersion);
        Assert.Equal("ctf", r.GameType);
        Assert.Equal(72u, r.MaximumNumberOfPlayers);
        Assert.False(r.HasPassword);
    }

    [Fact]
    public void Bfvietnam_reply_parses()
    {
        var r = Parse("bfvietnam_status.txt");

        Assert.Equal("*Easy Prey/Death*", r.HostName);
        Assert.Equal(15567u, r.HostPort);
        Assert.Equal("HUE [Choppers] -SSM-", r.MapName);
        Assert.Equal("bfvietnam", r.GameId);   // via game_id, not gameId
        Assert.Equal("BFVietnam", r.Mod);      // via map_id, not mapId -- the sync key
        Assert.Equal("v1.21", r.GameVersion);
        Assert.Equal("coop", r.GameType);
        Assert.Equal(50u, r.MaximumNumberOfPlayers);
        Assert.False(r.HasPassword);
    }

    /// <summary>
    /// BFV names the team ratios after its own factions and numbers tickets differently.
    /// </summary>
    [Fact]
    public void Bfvietnam_dialect_keys_map_onto_the_bf1942_properties()
    {
        var r = Parse("bfvietnam_status_populated.txt");

        Assert.Equal(574, r.Tickets1);          // tickets_t0
        Assert.Equal(139, r.Tickets2);          // tickets_t1
        Assert.Equal(1, r.AlliedTeamRatio);     // us_team_ratio
        Assert.Equal(1, r.AxisTeamRatio);       // nva_team_ratio
        Assert.Equal(60, r.TimeLimit);          // timelimit, not time_limit
        Assert.Equal(271, r.TicketRatio);
        Assert.Equal(75, r.GameStartDelay);
    }

    [Fact]
    public void Bfvietnam_players_parse_from_the_player_n_key()
    {
        var r = Parse("bfvietnam_status_populated.txt");

        Assert.Equal(3, r.Players.Count);
        Assert.Equal("rAFTERMAN", r.Players[0].Name);
        Assert.Equal(69u, r.Players[0].Kills);
        Assert.Equal(2, r.Players[0].Team);
        Assert.Equal("Son", r.Players[2].Name);
    }

    [Fact]
    public void Bf1942_players_parse_from_the_playername_n_key()
    {
        var r = Parse("bf1942_status.txt");

        Assert.Equal(26, r.Players.Count);
        Assert.Equal("Enot", r.Players[0].Name);
        Assert.Equal("Damien", r.Players[1].Name);
        Assert.Equal(41, r.Players[0].Score);
    }

    /// <summary>
    /// The keys BFV simply never sends must not take the whole reply down.
    /// </summary>
    [Fact]
    public void Keys_bfvietnam_never_sends_fall_back_to_defaults()
    {
        var r = Parse("bfvietnam_status.txt");

        Assert.Equal("", r.GameName);
        Assert.Equal(0, r.RoundTime);
        Assert.Equal(0, r.RoundTimeRemain);
        Assert.False(r.HitIndicator);
        Assert.Equal("", r.TkMode);
        Assert.Equal(0, r.AverageFps);
        Assert.Equal(0, r.ContentCheck);
        Assert.Empty(r.UnpureMods);
    }

    /// <summary>
    /// This live BF1942 CTF server omits ticket_ratio, which the original parser hard-indexed --
    /// it would have thrown and dropped the server from the list entirely.
    /// </summary>
    [Fact]
    public void Missing_optional_key_does_not_throw_on_bf1942_either()
    {
        var r = Parse("bf1942_status.txt");

        Assert.Equal(0, r.TicketRatio);
        Assert.Equal(40, r.TimeLimit);
        Assert.Equal("punish", r.TkMode);
        Assert.Equal(2400, r.RoundTime);
    }

    [Fact]
    public void Missing_required_key_still_throws()
    {
        var properties = Properties("bfvietnam_status.txt");
        properties.Remove("map_id");

        var ex = Assert.Throws<ProtocolViolationException>(() => new Bf1942QueryResult(properties));
        Assert.Contains("mapId/map_id", ex.Message);
    }

    [Fact]
    public void Malformed_value_still_throws()
    {
        var properties = Properties("bfvietnam_status.txt");
        properties["cpu"] = "not-a-number";

        Assert.Throws<ProtocolViolationException>(() => new Bf1942QueryResult(properties));
    }

    /// <summary>
    /// Replies span multiple packets; concatenating them must yield the same pairs as parsing each.
    /// </summary>
    [Fact]
    public void Concatenated_packets_parse_as_one()
    {
        var properties = GameSpyStatus.Parse("\\a\\1\\b\\2" + "\\c\\3\\d\\4");

        Assert.Equal("1", properties["a"]);
        Assert.Equal("2", properties["b"]);
        Assert.Equal("3", properties["c"]);
        Assert.Equal("4", properties["d"]);
    }
}
