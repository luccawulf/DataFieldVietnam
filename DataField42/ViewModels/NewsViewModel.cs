using CommunityToolkit.Mvvm.ComponentModel;
using DataField42.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace DataField42.ViewModels;

/// <summary>
/// Shows the announcement the central database is serving, with any pictures it refers to.
/// </summary>
/// <remarks>
/// Fetched once, when the user opens this page, and never again until they open it again. Nothing polls
/// and nothing runs in the background, so the client only ever talks to the server because someone
/// asked it to. If the server is unreachable that is not an error worth shouting about -- the page just
/// says so and everything else keeps working.
///
/// The whole post is laid out from the text first, pictures included as empty slots, and each picture
/// is filled in as it arrives. That keeps the author's ordering without any index arithmetic, and means
/// a slow or missing picture never holds up reading the words around it.
/// </remarks>
public partial class NewsViewModel : ObservableObject, IPageViewModel
{
    public string Title => "News";

    /// <summary>Shown instead of the post while fetching, or when there is nothing to show.</summary>
    [ObservableProperty]
    private string _status = "Fetching the latest news...";

    [ObservableProperty]
    private bool _hasStatus = true;

    public ObservableCollection<NewsItemViewModel> Items { get; } = new();

    public NewsViewModel(ILoggerFactory loggerFactory)
    {
        _ = Load(loggerFactory);
    }

    private async Task Load(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<NewsViewModel>();
        try
        {
            var communication = new DataField42Communication(loggerFactory.CreateLogger<DataField42Communication>());
            var newsService = new NewsService(communication, loggerFactory.CreateLogger<NewsService>());

            var news = await newsService.Fetch();
            if (string.IsNullOrWhiteSpace(news))
            {
                Status = "There is no news at the moment.";
                return;
            }

            // Lay the post out first, pictures as empty slots, so the words are readable immediately.
            var pending = new List<NewsItemViewModel>();
            foreach (var block in NewsService.Parse(news))
            {
                var item = block.Kind == NewsBlockKind.Text
                    ? NewsItemViewModel.ForText(block.Value)
                    : NewsItemViewModel.ForImage(block.Value);
                Items.Add(item);
                if (item.IsImage)
                    pending.Add(item);
            }

            HasStatus = false;

            foreach (var item in pending)
            {
                var bytes = await newsService.FetchImage(item.ImageName!);
                if (bytes != null)
                    item.Image = Decode(bytes, logger);

                // A picture that never arrives or will not decode leaves an empty slot, which the
                // template collapses -- the rest of the post is unaffected.
            }
        }
        catch (NotSupportedException)
        {
            logger.LogInformation("Server has no news feature.");
            Status = "This server doesn't provide news yet.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch news.");
            Status = "Couldn't reach the news server, so there is nothing to show right now.\n\n" +
                     "This doesn't affect downloading maps or joining servers.";
        }
    }

    /// <summary>
    /// Turns downloaded bytes into something drawable, or null if they are not a usable picture.
    /// </summary>
    /// <remarks>
    /// Decoding an image that came off the network is the one genuinely new thing this feature does,
    /// and image decoders are a classic place for malformed input to cause trouble. So the bytes are
    /// read from a private, non-writable buffer and decoded fully up front (OnLoad) rather than left
    /// streaming from it; the result is frozen so no half-built object can be handed to another
    /// thread; colour profiles are skipped, being extra parsing of attacker-supplied data for no
    /// benefit at this size; and the decode is capped by pixel width so a small file describing an
    /// enormous image cannot expand into hundreds of megabytes of bitmap.
    ///
    /// Anything that still goes wrong means the picture is not shown. It never takes the page with it.
    /// </remarks>
    private static BitmapImage? Decode(byte[] bytes, ILogger logger)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);

            // Read the header first to find out how big it claims to be. DecodePixelWidth scales in
            // both directions, so setting it unconditionally would blow a small banner up to the cap
            // and leave it blurry; it is only wanted when the picture is genuinely too large.
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var naturalWidth = frame.PixelWidth;
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (naturalWidth > MaximumDecodedWidth)
                image.DecodePixelWidth = MaximumDecodedWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"A news picture could not be displayed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// How wide a picture is allowed to be decoded, whatever the file claims.
    /// </summary>
    /// <remarks>
    /// This is a memory bound, not a layout one -- the page decides the displayed size from the window
    /// width. It has to be generous enough that a maximised window still shows something sharp, and
    /// tight enough that one file cannot turn into an enormous bitmap: compression means file size
    /// says nothing about pixel count, and an 8000x6000 picture is only 140 KB on disk but 192 MB
    /// decoded. At this cap the worst case is around 7 MB per picture.
    /// </remarks>
    private const int MaximumDecodedWidth = 1600;
}

/// <summary>One drawable piece of the news page: a run of text, or a picture.</summary>
public partial class NewsItemViewModel : ObservableObject
{
    public string? Text { get; private init; }

    /// <summary>The picture's file name, kept so it can be fetched after the layout is on screen.</summary>
    public string? ImageName { get; private init; }

    /// <summary>Null until the picture arrives, and stays null if it never does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private BitmapImage? _image;

    /// <summary>Drives the picture's visibility, so an empty slot leaves no gap in the page.</summary>
    public bool HasImage => Image != null;

    public bool IsImage => ImageName != null;
    public bool IsText => Text != null;

    public static NewsItemViewModel ForText(string text) => new() { Text = text };
    public static NewsItemViewModel ForImage(string name) => new() { ImageName = name };
}
