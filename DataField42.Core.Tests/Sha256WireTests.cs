namespace DataField42.Core.Tests;

/// <summary>
/// The SHA-256 field the server appends to each entry of the file listing.
/// </summary>
/// <remarks>
/// It is a sixth field on a record that used to have five, which is the whole reason it can be added
/// at all: the listing is parsed by position and older clients stop at the fifth, so they carry on
/// unaffected while newer ones pick up the extra digest. Both directions have to keep working -- a new
/// client meets old servers for as long as anyone is running one, and old clients meet this server
/// from the moment it is deployed.
/// </remarks>
public class Sha256WireTests
{
    private const string Digest = "41c1cfbc6b9cad3af7270e6ab97c8f54460c072e3fbefbe79a0ef649e46aaaac";

    private static FileInfo Parse(string line) => new(line.Split(' ').ToList());

    /// <summary>A server that sends the sixth field: the client picks the digest up.</summary>
    [Fact]
    public void A_listing_with_the_extra_field_yields_the_digest()
    {
        var fileInfo = Parse($"bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160 {Digest}");

        Assert.Equal(Digest, fileInfo.Sha256);
        Assert.Equal("1A2B3C4D", fileInfo.Checksum);   // the original field is untouched
        Assert.Equal(4096UL, fileInfo.Size);
    }

    /// <summary>A server that predates it: everything still parses, the digest is simply absent.</summary>
    [Fact]
    public void A_listing_without_the_extra_field_still_parses()
    {
        var fileInfo = Parse("bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160");

        Assert.Equal("", fileInfo.Sha256);
        Assert.Equal("1A2B3C4D", fileInfo.Checksum);
    }

    /// <summary>
    /// Anything that is not a well-formed digest is ignored rather than carried around and compared
    /// against later, which would turn a malformed listing into a verification that cannot pass.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("notadigest")]
    [InlineData("41c1cfbc")]                                  // too short
    [InlineData(Digest + "aa")]                               // too long
    [InlineData("g1c1cfbc6b9cad3af7270e6ab97c8f54460c072e3fbefbe79a0ef649e46aaaac")]   // not hex
    public void A_malformed_digest_field_is_ignored(string sixthField)
    {
        var fileInfo = Parse($"bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160 {sixthField}");

        Assert.Equal("", fileInfo.Sha256);
    }

    [Fact]
    public void Digests_are_normalised_to_lowercase_so_comparison_is_exact()
    {
        var fileInfo = Parse($"bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160 {Digest.ToUpperInvariant()}");

        Assert.Equal(Digest, fileInfo.Sha256);
    }

    /// <summary>
    /// The digest is not part of file identity: the per-file preamble sent before each transfer still
    /// carries only five fields, so it must still compare equal to its listing entry.
    /// </summary>
    [Fact]
    public void A_five_field_preamble_still_matches_its_six_field_listing_entry()
    {
        var listed = Parse($"bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160 {Digest}");
        var preamble = Parse("bfvietnam \"archives/bfvietnam/levels/berlin.rfa\" 1A2B3C4D 4096 1350905160");

        Assert.True(listed.IsEqualTo(preamble));
        Assert.True(preamble.IsEqualTo(listed));
    }

    [Fact]
    public void The_computed_digest_matches_the_framework_over_the_same_bytes()
    {
        var directory = Directory.CreateTempSubdirectory("dfv_sha_").FullName;
        try
        {
            foreach (var size in new[] { 0, 1, 4096, (1 << 20) + 7 })
            {
                var bytes = new byte[size];
                new Random(size).NextBytes(bytes);
                var path = System.IO.Path.Combine(directory, $"blob_{size}.bin");
                File.WriteAllBytes(path, bytes);

                var expected = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                Assert.Equal(expected, CheckSums.Sha256(path));
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
