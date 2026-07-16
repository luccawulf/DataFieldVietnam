namespace DataField42.Core.Tests;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A Battlefield Vietnam client cannot know what mod a server runs: its browser parses mapname,
/// gametype and hostport out of the query reply but never game_id. So someone sitting in base
/// BFVietnam who joins a DiceCity_V server makes the hook report "Mod: BFVietnam" -- honest about the
/// client, useless about the server. The server corrects that from the map, and the client has to
/// take the correction: without it VerifyFileList rejects a perfectly good offer, and the game gets
/// relaunched on the very mod it could not join with.
/// </summary>
public class DownloadManagerTests
{
    private static FileInfo Parse(string line) => new(line.Split(' '));

    /// <summary>
    /// A BFV server's offer for DiceCity_V/the_shootout, cut down to the lines that matter: the
    /// resolved chain is DiceCity_V -> BFVietnam, and only DiceCity_V ships the map.
    /// </summary>
    private static List<FileInfo> ShootoutOffer() =>
    [
        Parse("BFVietnam \"LevelCheck.con\" E3F1378D 12498 1780103395"),
        Parse("BFVietnam \"Archives/BfVietnam/game.rfa\" 33FBBAE8 119115 1095868318"),
        Parse("DiceCity_V \"Archives/texture.rfa\" 0E23D700 180794804 1116127290"),
        Parse("DiceCity_V \"Archives/BfVietnam/Levels/The_Shootout.rfa\" 50A69C21 4559739 1116194414"),
    ];

    /// <remarks>
    /// The communication and decision maker are untouched by the code under test, and
    /// DataField42Communication is concrete and socket-backed, so they stay null rather than drag a
    /// mocking framework in for two fields.
    /// </remarks>
    private static DownloadManager Make(string mod, string map, List<FileInfo> offer)
    {
        var downloadManager = new DownloadManager(null!, null!, null!, NullLogger<DownloadManager>.Instance);
        Set(downloadManager, "_mod", mod);
        Set(downloadManager, "_map", map);
        Set(downloadManager, "_fileInfos", offer);
        return downloadManager;
    }

    private static void Set(DownloadManager target, string field, object value) =>
        typeof(DownloadManager)
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(target, value);

    private static void Adopt(DownloadManager target) =>
        typeof(DownloadManager)
            .GetMethod("AdoptModFromOffer", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, null);

    [Fact]
    public void Mod_is_corrected_to_the_one_that_ships_the_map()
    {
        var downloadManager = Make("BFVietnam", "the_shootout", ShootoutOffer());

        Adopt(downloadManager);

        Assert.Equal("DiceCity_V", downloadManager.Mod);
    }

    [Fact]
    public void Corrected_mod_makes_the_offer_verify()
    {
        var downloadManager = Make("BFVietnam", "the_shootout", ShootoutOffer());

        Adopt(downloadManager);
        (var hasMod, var hasMap) = downloadManager.VerifyFileList();

        // Without the correction this is (true, false): BFVietnam is in the offer as DiceCity_V's
        // base so hasMod passes, but the map is DiceCity_V's, so hasMap fails and the user is told
        // "Server doesn't have the map: the_shootout".
        Assert.True(hasMod);
        Assert.True(hasMap);
    }

    [Fact]
    public void A_request_that_already_ships_the_map_is_left_alone()
    {
        // The ordinary missing-map sync, which already worked and must keep working.
        var downloadManager = Make("DiceCity_V", "the_shootout", ShootoutOffer());

        Adopt(downloadManager);

        Assert.Equal("DiceCity_V", downloadManager.Mod);
    }

    [Fact]
    public void Wildcard_map_is_left_alone()
    {
        // The "mod" argument form asks for a whole mod with map "*"; there is no map to resolve from.
        var downloadManager = Make("BFVietnam", "*", ShootoutOffer());

        Adopt(downloadManager);

        Assert.Equal("BFVietnam", downloadManager.Mod);
    }

    [Fact]
    public void An_offer_without_the_map_is_left_alone()
    {
        var downloadManager = Make(
            "BFVietnam",
            "the_shootout",
            [Parse("BFVietnam \"Archives/BfVietnam/game.rfa\" 33FBBAE8 119115 1095868318")]);

        Adopt(downloadManager);

        Assert.Equal("BFVietnam", downloadManager.Mod);
    }

    [Fact]
    public void Patch_numbered_level_still_matches()
    {
        // Maps ship as Hue.rfa + Hue_001.rfa; the patch suffix is not part of the name.
        var downloadManager = Make(
            "BFVietnam",
            "the_shootout",
            [Parse("DiceCity_V \"Archives/BfVietnam/Levels/The_Shootout_001.rfa\" 50A69C21 4559739 1116194414")]);

        Adopt(downloadManager);

        Assert.Equal("DiceCity_V", downloadManager.Mod);
    }
}
