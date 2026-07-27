using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

/// <summary>
/// Fetches the announcement text the central database is serving, and any pictures it refers to.
/// </summary>
/// <remarks>
/// Deliberately pull-only, and only when the user actually opens the news: there is no background
/// process and nothing is ever pushed, so this is a page the client goes and reads rather than a live
/// channel into someone's machine. Plain text rather than a structured format, so whoever runs the
/// server can write it however they like and a malformed file can never break a client.
///
/// Pictures are named in the text as [img:name.png] and fetched from the same server the text came
/// from. They are deliberately not web links: a link would make every client that opens this page
/// contact a third-party host and hand it the player's IP address, which is exactly the fanning-out
/// this feature was meant to avoid.
/// </remarks>
public class NewsService(DataField42Communication communication, ILogger<NewsService> logger)
{
    /// <summary>What a server replies with when it does not know a command at all.</summary>
    private const string UnknownCommand = "unknown identifier";

    /// <summary>What a server replies when it knows the command but has no such picture.</summary>
    private const string ImageUnavailable = "image not available";

    /// <summary>
    /// Refuse anything bigger without reading it. The server caps this too, but the client cannot
    /// assume it is talking to a well-behaved server -- the size arrives before the bytes do, so this
    /// costs nothing and stops a hostile reply from filling memory.
    /// </summary>
    public const int MaximumImageBytes = 4 * 1024 * 1024;

    /// <summary>How many pictures one news post may show, so a long list cannot stall the page.</summary>
    public const int MaximumImages = 12;

    /// <summary>[img:name.png] alone on a line.</summary>
    /// <remarks>
    /// The trailing class allows a carriage return because news.txt is written by hand and may well be
    /// edited on Windows. In multiline mode $ matches before the \n, which leaves the \r sitting just
    /// before it, so a class of only spaces and tabs quietly fails to match every line of a CRLF file
    /// -- and the whole post would render as text with the references still in it.
    /// </remarks>
    private static readonly Regex ImageReference =
        new(@"^[ \t]*\[img:[ \t]*(?<name>[A-Za-z0-9._-]+)[ \t]*\][ \t\r]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<string> Fetch()
    {
        logger.LogDebug("Requesting news from the central database.");
        communication.StartSession();
        communication.SendString("news");
        var news = await communication.ReceiveString();

        // A server older than the news feature answers every unrecognised command this way. Catch it
        // here rather than letting the raw protocol reply end up on screen as if it were the news.
        if (news == UnknownCommand)
        {
            logger.LogInformation("Server does not support news.");
            throw new NotSupportedException("This server does not serve news.");
        }

        logger.LogDebug($"Received {news.Length} characters of news.");
        return news;
    }

    /// <summary>
    /// Splits news text into the pieces the page draws: runs of text, and the pictures between them.
    /// </summary>
    /// <remarks>
    /// Text that names no pictures comes back as a single block, which is what every existing news
    /// post does -- so a server that has never heard of pictures keeps working unchanged.
    /// </remarks>
    public static IReadOnlyList<NewsBlock> Parse(string news)
    {
        var blocks = new List<NewsBlock>();
        var position = 0;
        var images = 0;

        foreach (Match match in ImageReference.Matches(news))
        {
            if (images >= MaximumImages)
                break;

            AddText(news[position..match.Index]);
            blocks.Add(new NewsBlock(NewsBlockKind.Image, match.Groups["name"].Value));
            images++;
            position = match.Index + match.Length;
        }

        AddText(news[position..]);
        return blocks;

        void AddText(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                blocks.Add(new NewsBlock(NewsBlockKind.Text, text.Trim('\r', '\n')));
        }
    }

    /// <summary>Fetches one picture, or null when the server has not got it.</summary>
    /// <remarks>
    /// Never throws for an absent or oversized picture: a news post that names a file the server does
    /// not have should show the rest of the post, not an error page.
    /// </remarks>
    public async Task<byte[]?> FetchImage(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            communication.StartSession();
            communication.SendString($"newsImage {name}");

            // The reply is either a size or a refusal, and both arrive as a length-prefixed string.
            var reply = await communication.ReceiveString();
            if (reply == UnknownCommand || reply == ImageUnavailable)
            {
                logger.LogInformation($"Server has no news image '{name}'.");
                return null;
            }

            if (!ulong.TryParse(reply, out var size))
            {
                logger.LogWarning($"Unexpected reply asking for news image '{name}': {reply}");
                return null;
            }

            if (size == 0 || size > MaximumImageBytes)
            {
                logger.LogWarning($"News image '{name}' is {size} bytes, which is outside what will be shown.");
                return null;
            }

            communication.SendAcknowledgement();

            using var buffer = new MemoryStream((int)size);
            await communication.ReceiveFile(size, buffer, new DownloadBackgroundWorker(size), cancellationToken);
            communication.SendAcknowledgement();

            logger.LogDebug($"Received news image '{name}' ({size} bytes).");
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, $"Could not fetch news image '{name}'.");
            return null;
        }
    }
}

public enum NewsBlockKind
{
    Text,
    Image,
}

/// <summary>One piece of a news post: a run of text, or the name of a picture to show.</summary>
public record NewsBlock(NewsBlockKind Kind, string Value);
