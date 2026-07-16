using System.Reflection;

namespace DataField42.Core.Tests;

/// <summary>
/// The "bfvmap" argument form the Battlefield Vietnam hook emits.
/// </summary>
/// <remarks>
/// This is the seam between hand-written assembly and C#: the hook builds the string with a nibble
/// loop and nothing checks it at compile time, so a mismatch here would only ever show up as a
/// mis-parsed address at the moment a player fails to join. The hex values below are the ones
/// observed live in the game's sockaddr_in (188.245.154.232:15569).
///
/// ParseArgumentsFromBfVietnam reads the CD-key from the registry, which is machine-specific, so the
/// address decoding is exercised directly rather than through Parse().
/// </remarks>
public class CommandLineArgumentsTests
{
    private static (string Ip, int Port) DecodeAddress(string ipPortHex)
    {
        // Mirrors the decode half of ParseArgumentsFromBfVietnam without the registry read.
        var parts = ipPortHex.Split(':');
        Assert.Equal(2, parts.Length);
        var ip = string.Join('.', Enumerable.Range(0, 4)
            .Select(i => Convert.ToInt32(parts[0].Substring(i * 2, 2), 16)));
        return (ip, Convert.ToInt32(parts[1], 16));
    }

    [Theory]
    [InlineData("BCF59AE8:3CD1", "188.245.154.232", 15569)]  // the user's live server
    [InlineData("7F000001:3CCF", "127.0.0.1", 15567)]        // loopback, BFV's default port
    [InlineData("0A000001:3CD1", "10.0.0.1", 15569)]
    [InlineData("FFFFFFFF:FFFF", "255.255.255.255", 65535)]
    public void Hook_address_hex_decodes(string hex, string expectedIp, int expectedPort)
    {
        var (ip, port) = DecodeAddress(hex);
        Assert.Equal(expectedIp, ip);
        Assert.Equal(expectedPort, port);
    }

    /// <summary>
    /// The hook writes hex with 'A'-10, i.e. uppercase. Lowercase would mean the nibble loop changed.
    /// </summary>
    [Fact]
    public void Live_capture_round_trips()
    {
        // sockaddr_in bytes seen at ws2_32!sendto: port 3c d1, addr bc f5 9a e8 (both network order)
        var portBytes = new byte[] { 0x3c, 0xd1 };
        var addrBytes = new byte[] { 0xbc, 0xf5, 0x9a, 0xe8 };

        var hex = Convert.ToHexString(addrBytes) + ":" + Convert.ToHexString(portBytes);
        Assert.Equal("BCF59AE8:3CD1", hex);

        var (ip, port) = DecodeAddress(hex);
        Assert.Equal("188.245.154.232", ip);
        Assert.Equal(15569, port);
    }

    /// <summary>
    /// The hook sends the whole map path the game holds, not a bare name.
    /// </summary>
    /// <remarks>
    /// Observed live: the first hook firing produced "BfVietnam/levels/the_shootout/" and was
    /// rejected as an illegal map name. Mixed case, and the trailing slash matters.
    /// </remarks>
    [Theory]
    [InlineData("BfVietnam/levels/the_shootout/", "the_shootout")]
    [InlineData("BfVietnam/levels/The_City/", "The_City")]
    [InlineData("bfvietnam/levels/hue/", "hue")]
    [InlineData("The_City", "The_City")]                 // a bare name still works
    public void Hook_map_path_prefix_is_stripped(string sent, string expected)
    {
        var prefix = (string)typeof(CommandLineArguments)
            .GetField("MapPathPrefix", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        var map = sent;
        if (map.ToLower().StartsWith(prefix))
            map = map[prefix.Length..^1];

        Assert.Equal(expected, map);
        // whatever survives must satisfy the name rules ParseArguments enforces
        Assert.Matches($"^[{FileInfo.AllowableChars}]*$", map);
    }

    /// <summary>
    /// The hook names the mod by its addModPath entry, since that list is the only place the game
    /// holds it. Observed live: "Mods/DiceCity_V/".
    /// </summary>
    [Theory]
    [InlineData("Mods/DiceCity_V/", "DiceCity_V")]
    [InlineData("Mods/BFVietnam/", "BFVietnam")]
    [InlineData("mods/PoE/", "PoE")]
    [InlineData("DiceCity_V", "DiceCity_V")]            // a bare name still works
    public void Hook_mod_path_prefix_is_stripped(string sent, string expected)
    {
        var prefix = (string)typeof(CommandLineArguments)
            .GetField("ModPathPrefix", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        var mod = sent;
        if (mod.ToLower().StartsWith(prefix))
            mod = mod[prefix.Length..].TrimEnd('/');

        Assert.Equal(expected, mod);
        Assert.Matches($"^[{FileInfo.AllowableChars}]*$", mod);
    }

    [Theory]
    [InlineData("BCF59AE8")]          // no port
    [InlineData("BCF59AE:3CD1")]      // ip too short
    [InlineData("BCF59AE8:3CD")]      // port too short
    [InlineData("BCF59AE8:3CD1:1")]   // too many parts
    [InlineData("")]
    public void Malformed_address_is_rejected(string hex)
    {
        var parse = typeof(CommandLineArguments).GetMethod(
            "ParseArgumentsFromBfVietnam", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);

        var ex = Assert.Throws<TargetInvocationException>(
            () => parse!.Invoke(null, new object[] { hex, "The_City", "DiceCity_V" }));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    /// <summary>
    /// "bfvmap" has to be recognised, or the hook's launch falls through to the dashboard.
    /// </summary>
    [Fact]
    public void Bfvmap_is_recognised_as_a_sync_and_join()
    {
        CommandLineArguments.Parse(["DataField42.exe", "bfvmap", "BCF59AE8:3CD1", "The_City", "DiceCity_V"]);
        Assert.Equal(CommandLineArgumentIdentifier.SyncAndJoinServer, CommandLineArguments.Identifier);
    }
}
