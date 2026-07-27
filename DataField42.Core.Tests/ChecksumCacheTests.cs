namespace DataField42.Core.Tests;

/// <summary>
/// The digest cache, and the separation between cached lookups and real verification.
/// </summary>
/// <remarks>
/// The cache exists to keep syncs fast, and being wrong in it should only ever cost a needless
/// download. What it must never do is answer on behalf of a file that arrived over the network, which
/// is what used to happen: the key was size and last-modified time, both of which the server supplies,
/// so a server could name values matching a record the client already held and have its own file
/// "verified" without being read.
/// </remarks>
public class ChecksumCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dfv_cache_tests_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string contents, DateTime lastWrite)
    {
        var path = System.IO.Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTime(path, lastWrite);
        return path;
    }

    /// <summary>
    /// Two different files, same size, same timestamp -- exactly what a hostile server would arrange.
    /// </summary>
    [Fact]
    public void Files_sharing_a_size_and_timestamp_do_not_share_a_digest()
    {
        var stamp = new DateTime(2004, 3, 15, 12, 0, 0, DateTimeKind.Local);
        var genuine = WriteFile("genuine.rfa", "AAAABBBBCCCCDDDD", stamp);
        var swapped = WriteFile("swapped.rfa", "XXXXYYYYZZZZWWWW", stamp);

        Assert.Equal(
            new System.IO.FileInfo(genuine).Length,
            new System.IO.FileInfo(swapped).Length);

        // Seeds the cache for the first file, then asks about the second.
        var first = CheckSums.Crc32CWithCache(genuine);
        var second = CheckSums.Crc32CWithCache(swapped);

        Assert.NotEqual(first, second);
        Assert.Equal(CheckSums.Crc32C(swapped), second);
    }

    /// <summary>
    /// The verification digest must come from the bytes, never from a record about them.
    /// </summary>
    [Fact]
    public void The_uncached_digest_always_reflects_the_current_contents()
    {
        var stamp = new DateTime(2004, 3, 15, 12, 0, 0, DateTimeKind.Local);
        var path = WriteFile("archive.rfa", "the real contents", stamp);

        // Prime the cache, then rewrite the file keeping size and timestamp identical.
        var cachedBefore = CheckSums.Crc32CWithCache(path);
        File.WriteAllText(path, "the FAKE contents");
        File.SetLastWriteTime(path, stamp);

        Assert.Equal(cachedBefore, CheckSums.Crc32CWithCache(path));   // the cache is still stale...
        Assert.NotEqual(cachedBefore, CheckSums.Crc32C(path));         // ...but verification is not
    }

    [Fact]
    public void Digests_match_a_whole_buffer_computation_across_the_streaming_block_size()
    {
        // Either side of the 1 MB read block, so a boundary bug in the chunked loop would show up.
        foreach (var size in new[] { 0, 1, 1024, (1 << 20) - 1, 1 << 20, (1 << 20) + 1, (1 << 21) + 12345 })
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            var path = System.IO.Path.Combine(_directory, $"blob_{size}.bin");
            File.WriteAllBytes(path, bytes);

            Assert.Equal(Force.Crc32.Crc32CAlgorithm.Compute(bytes), CheckSums.Crc32C(path));
        }
    }

    [Fact]
    public void A_record_is_reused_only_for_the_same_path_size_and_time()
    {
        var repository = new ChecksumRepository(System.IO.Path.Combine(_directory, "cache.yaml"));
        repository.AddRecord(@"C:\game\mods\bfvietnam\archives\texture.rfa", "1234", 500, 1000);

        Assert.True(repository.FindChecksum(@"C:\game\mods\bfvietnam\archives\texture.rfa", 500, 1000).Item1);
        Assert.False(repository.FindChecksum(@"C:\game\mods\other\archives\texture.rfa", 500, 1000).Item1);
        Assert.False(repository.FindChecksum(@"C:\game\mods\bfvietnam\archives\texture.rfa", 501, 1000).Item1);
        Assert.False(repository.FindChecksum(@"C:\game\mods\bfvietnam\archives\texture.rfa", 500, 1001).Item1);
    }

    /// <summary>Records written before the path was part of the key must never be matched.</summary>
    [Fact]
    public void Records_from_the_old_pathless_format_are_discarded()
    {
        var path = System.IO.Path.Combine(_directory, "legacy.yaml");
        File.WriteAllText(path, """
            - Checksum: '1629132444'
              Size: 11780647
              LastTimeModified: 1350905160
            """);

        var repository = new ChecksumRepository(path);

        Assert.False(repository.FindChecksum("anything.rfa", 11780647, 1350905160).Item1);
    }

    /// <summary>A corrupt or absent cache costs a recompute, never a crash.</summary>
    [Theory]
    [InlineData("this is not yaml: [ unclosed")]
    [InlineData("")]
    public void An_unreadable_cache_is_treated_as_empty(string contents)
    {
        var path = System.IO.Path.Combine(_directory, "broken.yaml");
        File.WriteAllText(path, contents);

        var repository = new ChecksumRepository(path);

        Assert.False(repository.FindChecksum("a.rfa", 1, 2).Item1);
        repository.AddRecord("a.rfa", "99", 1, 2);
        Assert.True(repository.FindChecksum("a.rfa", 1, 2).Item1);
    }

    [Fact]
    public void Saved_records_survive_a_reload()
    {
        var path = System.IO.Path.Combine(_directory, "roundtrip.yaml");
        var repository = new ChecksumRepository(path);
        repository.AddRecord(@"C:\game\a.rfa", "4242", 77, 88);
        repository.Save();

        Assert.Equal("4242", new ChecksumRepository(path).FindChecksum(@"C:\game\a.rfa", 77, 88).Item2);
    }
}
