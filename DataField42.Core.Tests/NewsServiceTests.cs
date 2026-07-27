namespace DataField42.Core.Tests;

/// <summary>
/// Splitting a news post into the text and pictures the page draws.
/// </summary>
/// <remarks>
/// The parser reads a file the server operator writes by hand, so the thing that matters most is that
/// odd input degrades into plain text rather than losing the post. Anything it does not recognise as a
/// picture reference has to survive as words on screen.
/// </remarks>
public class NewsServiceTests
{
    private static IReadOnlyList<NewsBlock> Parse(string news) => NewsService.Parse(news);

    [Fact]
    public void A_post_with_no_pictures_is_one_run_of_text()
    {
        var blocks = Parse("Server is back up.\nNew map rotation this weekend.");

        var block = Assert.Single(blocks);
        Assert.Equal(NewsBlockKind.Text, block.Kind);
        Assert.Equal("Server is back up.\nNew map rotation this weekend.", block.Value);
    }

    [Fact]
    public void A_picture_splits_the_text_around_it_and_keeps_the_order()
    {
        var blocks = Parse("""
            New map is live.

            [img:hue_city.png]

            Join us Friday at 8.
            """);

        Assert.Collection(blocks,
            first => { Assert.Equal(NewsBlockKind.Text, first.Kind); Assert.Contains("New map is live.", first.Value); },
            second => { Assert.Equal(NewsBlockKind.Image, second.Kind); Assert.Equal("hue_city.png", second.Value); },
            third => { Assert.Equal(NewsBlockKind.Text, third.Kind); Assert.Contains("Friday", third.Value); });
    }

    [Fact]
    public void Several_pictures_all_come_back_in_order()
    {
        var blocks = Parse("[img:one.png]\n[img:two.jpg]\n[img:three.jpeg]");

        Assert.Equal(
            new[] { "one.png", "two.jpg", "three.jpeg" },
            blocks.Where(x => x.Kind == NewsBlockKind.Image).Select(x => x.Value));
    }

    [Theory]
    [InlineData("  [img:spaced.png]  ")]
    [InlineData("[img: inner.png ]")]
    [InlineData("[IMG:shouty.PNG]")]
    public void Reference_syntax_is_forgiving_about_spacing_and_case(string line)
    {
        Assert.Contains(Parse(line), x => x.Kind == NewsBlockKind.Image);
    }

    /// <summary>
    /// news.txt is written by hand and may be edited on Windows, so it can arrive with CRLF endings.
    /// </summary>
    /// <remarks>
    /// Regression test. In multiline mode $ matches before the \n, leaving \r just before it, so a
    /// pattern ending in [ \t]*$ matched nothing at all in a CRLF file and the whole post rendered as
    /// plain text with the [img:...] lines still showing.
    /// </remarks>
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void References_are_found_whatever_the_line_endings(string newLine)
    {
        var news = string.Join(newLine, "Look at this.", "", "[img:hue_city.png]", "", "Good, no?");

        var blocks = Parse(news);

        Assert.Equal(3, blocks.Count);
        Assert.Equal("hue_city.png", blocks.Single(x => x.Kind == NewsBlockKind.Image).Value);
    }

    /// <summary>Anything that is not a reference on its own line stays as words.</summary>
    [Theory]
    [InlineData("see [img:inline.png] there")]           // not alone on the line
    [InlineData("[img:../../etc/passwd]")]               // path separators are not name characters
    [InlineData(@"[img:sub\dir\x.png]")]
    [InlineData("[img:]")]                               // no name
    [InlineData("[image:x.png]")]                        // not the keyword
    [InlineData("[img:has space.png]")]
    public void Anything_else_is_left_as_text(string news)
    {
        var blocks = Parse(news);

        Assert.All(blocks, block => Assert.Equal(NewsBlockKind.Text, block.Kind));
        Assert.Contains(news.Trim(), string.Concat(blocks.Select(x => x.Value)));
    }

    /// <summary>A post cannot make the page fetch an unbounded number of files.</summary>
    [Fact]
    public void The_number_of_pictures_is_capped()
    {
        var news = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"[img:pic{i}.png]"));

        Assert.Equal(NewsService.MaximumImages, Parse(news).Count(x => x.Kind == NewsBlockKind.Image));
    }

    [Fact]
    public void Blank_text_between_pictures_is_not_kept_as_an_empty_block()
    {
        var blocks = Parse("[img:a.png]\n\n   \n\n[img:b.png]");

        Assert.All(blocks, block => Assert.Equal(NewsBlockKind.Image, block.Kind));
        Assert.Equal(2, blocks.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void An_empty_post_produces_nothing_to_draw(string news)
    {
        Assert.Empty(Parse(news));
    }

    /// <summary>
    /// Reporting progress against an undeclared total must not take the transfer down with it.
    /// </summary>
    /// <remarks>
    /// This is a regression test. Fetching a news picture passed a progress worker whose TotalSize was
    /// never set, and the percentage calculation divided by zero -- so every picture failed to load,
    /// while the server was serving them perfectly. The transfer never needed the percentage at all.
    /// </remarks>
    [Fact]
    public void Progress_reporting_without_a_total_size_does_not_throw()
    {
        var worker = new DownloadBackgroundWorker();
        var reported = 0;
        worker.ProgressChanged += _ => reported++;

        worker.ReportProgressAmount(4096);
        worker.ReportProgressAmount(4096);

        Assert.Equal(0, reported);   // nothing to report a percentage against
    }

    [Fact]
    public void Progress_reporting_with_a_total_size_still_reports()
    {
        var worker = new DownloadBackgroundWorker(1000);
        var percentages = new List<int>();
        worker.ProgressChanged += percentage => percentages.Add(percentage);

        worker.ReportProgressAmount(250);
        worker.ReportProgressAmount(250);
        worker.ReportProgressAmount(500);

        Assert.Equal(new[] { 25, 50, 100 }, percentages);
    }
}
