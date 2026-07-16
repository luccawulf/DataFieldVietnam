using System.Globalization;
using System.Text.RegularExpressions;

public static class CommandLineArguments
{
    public static CommandLineArgumentIdentifier Identifier { get; set; }
    public static string Mod { get; set; } = "BF1942";
    /// <summary>
    /// Map name with underscores
    /// </summary>
    public static string Map { get; set; }
    public static string KeyHash { get; set; }
    public static string Ip { get; set; }
    public static int Port { get; set; }
    public static string Password { get; set; }

    public static string? RawString => Environment.GetCommandLineArgs().Length > 1 ? "\"" + string.Join("\" \"", Environment.GetCommandLineArgs()[1..]) + "\"" : "";

    public static void Parse(string[] arguments)
    {
        // DownloadAndJoinServer
        if(arguments.Length >= 2)
        {
            if (arguments[1] == "map" || arguments[1] == "mod" || arguments[1] == "bfvmap" || arguments[1] == "SyncAndJoinServer")
                Identifier = CommandLineArgumentIdentifier.SyncAndJoinServer;
            else if (arguments[1] == "install")
                Identifier = CommandLineArgumentIdentifier.Install;
            else
                Identifier = CommandLineArgumentIdentifier.Unknown;
        }
        else
        {
            Identifier = CommandLineArgumentIdentifier.None;
        }

        if (arguments.Length == 7 && arguments[1] == "map")
            ParseArgumentsFromBf1942(arguments[2], arguments[3], arguments[4], arguments[5], arguments[6]);
        else if (arguments.Length == 6 && arguments[1] == "mod") // TODO: patch asm to have * for map path for mod
            ParseArgumentsFromBf1942(arguments[2], arguments[3], arguments[4], "*", arguments[5]);
        else if (arguments.Length == 5 && arguments[1] == "bfvmap")
            ParseArgumentsFromBfVietnam(arguments[2], arguments[3], arguments[4]);
        else if (arguments.Length == 8 && arguments[1] == "SyncAndJoinServer")
            ParseArguments(arguments[6], $"{arguments[4]}:{arguments[5]}", arguments[7], arguments[3], arguments[2]);
    }

    /// <summary>
    /// The map path the game hands over looks like "bfvietnam/levels/&lt;map&gt;/", so the map name is
    /// what is left once the prefix and the trailing slash are taken off.
    /// </summary>
    private const string MapPathPrefix = "bfvietnam/levels/";

    /// <summary>
    /// Prefix of an addModPath entry, which is how the Battlefield Vietnam hook names the active mod.
    /// </summary>
    private const string ModPathPrefix = "mods/";

    private static void ParseArgumentsFromBf1942(string keyRegisterPath, string ipPort, string password, string mapath, string mod)
    {
        var key = Registry.ReadKey(keyRegisterPath);

        if (mapath != "*")
            if (!(mapath.ToLower().StartsWith(MapPathPrefix) && mapath.EndsWith("/")))
                throw new ArgumentException($"Server has send an illegal map path: {mapath}");

        var map = mapath == "*" ? mapath : mapath[MapPathPrefix.Length..^1];

        ParseArguments(Md5.Hash(key), ipPort, password, map, mod);
    }

    /// <summary>
    /// The CD-key registry path, for the arguments the Battlefield Vietnam hook sends.
    /// </summary>
    /// <remarks>
    /// The hook does not pass this the way BF1942's does. BFV's path contains spaces, so sending it
    /// would mean copying and quoting a 57-character string in assembly; the client already knows it,
    /// so the hook sends only what it alone can know. Matches the string in BfVietnam.exe at 0x74400C
    /// that <see cref="Bf1942Client.GetKeyRegistryPath"/> reads.
    /// </remarks>
    private const string BfVietnamKeyRegistryPath = @"SOFTWARE\Electronic Arts\EA Games\Battlefield Vietnam\ergc";

    /// <summary>
    /// Parses the arguments sent by the Battlefield Vietnam hook: "bfvmap &lt;ipPortHex&gt; &lt;map&gt; &lt;mod&gt;".
    /// </summary>
    /// <remarks>
    /// The address arrives as hex (e.g. "BCF59AE8:3CD1") rather than dotted-decimal because the game
    /// only ever holds it as the four raw bytes of a sockaddr_in, and formatting decimal in assembly
    /// costs far more than parsing hex costs here. Both halves are network byte order, so they read
    /// left to right.
    ///
    /// The map arrives as the whole path the game holds -- "BfVietnam/levels/the_shootout/" -- not as
    /// a bare name, so the prefix comes off here rather than in the hook.
    /// </remarks>
    private static void ParseArgumentsFromBfVietnam(string ipPortHex, string map, string mod)
    {
        var parts = ipPortHex.Split(':');
        if (parts.Length != 2 || parts[0].Length != 8 || parts[1].Length != 4)
            throw new ArgumentException($"Game has sent an illegal address: {ipPortHex}");

        int ipByte(int index)
        {
            if (!int.TryParse(parts[0].AsSpan(index * 2, 2), NumberStyles.HexNumber, null, out var value))
                throw new ArgumentException($"Game has sent an illegal address: {ipPortHex}");
            return value;
        }

        var ip = $"{ipByte(0)}.{ipByte(1)}.{ipByte(2)}.{ipByte(3)}";

        if (!int.TryParse(parts[1], NumberStyles.HexNumber, null, out var port) || port == 0)
            throw new ArgumentException($"Game has sent an illegal port: {parts[1]}");

        if (map.ToLower().StartsWith(MapPathPrefix))
        {
            if (!map.EndsWith("/"))
                throw new ArgumentException($"Game has sent an illegal map path: {map}");
            map = map[MapPathPrefix.Length..^1];
        }

        // The mod arrives as its addModPath entry -- "Mods/DiceCity_V/" -- since that list is the only
        // place the game names the active mod. Same shape the server's get_relevant_mod_names parses.
        if (mod.ToLower().StartsWith(ModPathPrefix))
            mod = mod[ModPathPrefix.Length..].TrimEnd('/');

        var key = Registry.ReadKey(BfVietnamKeyRegistryPath);

        // The hook sends no password: it is not stored anywhere the hook can reach, and an empty one
        // is correct for every unpassworded server.
        ParseArguments(Md5.Hash(key), $"{ip}:{port}", "", map, mod);
    }

    private static void ParseArguments(string keyHash, string ipPort, string password, string map, string mod)
    {
        if (map != "*")
            if (!(Regex.IsMatch(map, $"^[{FileInfo.AllowableChars}]*$") && map.Length >= 1)) // only letters digits and underscores and hyphens and at least 1 char
                throw new ArgumentException($"Server has send an illegal map name: {map}");

        if (!(Regex.IsMatch(mod, $"^[{FileInfo.AllowableChars}]*$") && mod.Length >= 1)) // only letters digits and underscores and hyphens and at least 1 char
            throw new ArgumentException($"Server has send an illegal mod name: {mod}");

        KeyHash = keyHash;
        Ip = ipPort.Split(':')[0];
        Port = int.Parse(ipPort.Split(':')[1]);
        Password = password;
        Map = map;
        Mod = mod;
    }
}