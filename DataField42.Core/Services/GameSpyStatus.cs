/// <summary>
/// Splitting of a GameSpy \status\ reply into its backslash-delimited key/value pairs.
/// </summary>
/// <remarks>
/// Kept apart from the socket code so a captured reply can be parsed in a test without a live
/// server. Replies arrive as one or more packets, each starting with a backslash and carrying a
/// whole number of pairs, so concatenating them and splitting once gives the same result as
/// splitting each packet in turn.
/// </remarks>
public static class GameSpyStatus
{
    /// <summary>
    /// Merges the pairs found in <paramref name="data"/> into <paramref name="properties"/>.
    /// Later packets win, which is how duplicated keys (sv_punkbuster, content_check) resolve.
    /// </summary>
    public static void Merge(string data, Dictionary<string, string> properties)
    {
        var parts = data.Split("\\");
        for (int i = 0; i < parts.Length / 2; i++)
        {
            var key = parts[i * 2 + 1];
            if (key.Length > 0)
                properties[key] = parts[i * 2 + 2];
        }
    }

    public static Dictionary<string, string> Parse(string data)
    {
        Dictionary<string, string> properties = new();
        Merge(data, properties);
        return properties;
    }
}
