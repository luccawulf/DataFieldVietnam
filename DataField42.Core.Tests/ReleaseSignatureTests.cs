using System.Security.Cryptography;
using System.Text;

namespace DataField42.Core.Tests;

/// <summary>
/// Verification of signed release executables.
/// </summary>
/// <remarks>
/// This is the check that decides whether an executable downloaded over an unauthenticated connection
/// gets to run, so what matters here is not that a good signature passes but that every bad one fails.
/// Each test below is a way an attacker could try to get code accepted.
/// </remarks>
public class ReleaseSignatureTests : IDisposable
{
    private readonly ECDsa _releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _directory =
        Directory.CreateTempSubdirectory("dfv_sig_tests_").FullName;

    private string PublicKey => Convert.ToBase64String(_releaseKey.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _releaseKey.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string WriteFile(string name, string contents)
    {
        var path = System.IO.Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>Signs exactly as dfvsign does, so these tests exercise the real document format.</summary>
    private string SignDocument(string fileName, Version version, string filePath, ECDsa? key = null)
    {
        var manifest = new ReleaseSignature.Manifest(
            fileName, version, ReleaseSignature.ComputeSha256Hex(filePath));
        var signature = (key ?? _releaseKey)
            .SignData(Encoding.ASCII.GetBytes(manifest.ToLine()), HashAlgorithmName.SHA256);
        return $"{manifest.ToLine()}\n{Convert.ToBase64String(signature)}\n";
    }

    [Fact]
    public void A_genuine_release_verifies()
    {
        var path = WriteFile("DataFieldVietnam.exe", "the real client");
        var document = SignDocument("DataFieldVietnam.exe", new Version("2.1.0.2"), path);

        ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", new Version("2.1.0.1"), PublicKey);
    }

    /// <summary>The server swaps the executable but replays the signature it legitimately has.</summary>
    [Fact]
    public void Tampering_with_the_file_fails()
    {
        var path = WriteFile("DataFieldVietnam.exe", "the real client");
        var document = SignDocument("DataFieldVietnam.exe", new Version("2.1.0.2"), path);

        File.WriteAllText(path, "malware");

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", null, PublicKey));
        Assert.Contains("does not match its signature", ex.Message);
    }

    /// <summary>The digest is rewritten to match the malware -- but the signature covers the digest.</summary>
    [Fact]
    public void Rewriting_the_digest_in_the_manifest_fails()
    {
        var path = WriteFile("DataFieldVietnam.exe", "the real client");
        var document = SignDocument("DataFieldVietnam.exe", new Version("2.1.0.2"), path);

        File.WriteAllText(path, "malware");
        var lines = document.Split('\n');
        var forged = ReleaseSignature.ParseManifest(lines[0]) with
        {
            Sha256Hex = ReleaseSignature.ComputeSha256Hex(path),
        };

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, $"{forged.ToLine()}\n{lines[1]}\n", "DataFieldVietnam.exe", null, PublicKey));
        Assert.Contains("not valid", ex.Message);
    }

    /// <summary>Someone signs their own build with their own key.</summary>
    [Fact]
    public void A_signature_from_a_different_key_fails()
    {
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = WriteFile("DataFieldVietnam.exe", "malware");
        var document = SignDocument("DataFieldVietnam.exe", new Version("9.9.9.9"), path, attackerKey);

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", null, PublicKey));
        Assert.Contains("not valid", ex.Message);
    }

    /// <summary>A genuinely signed updater served in place of the client, or the other way round.</summary>
    [Fact]
    public void A_signature_for_a_different_file_fails()
    {
        var path = WriteFile("DataFieldVietnam.exe", "the real updater");
        var document = SignDocument("DataFieldVietnam_updater.exe", new Version("2.1.0.2"), path);

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", null, PublicKey));
        Assert.Contains("DataFieldVietnam_updater.exe", ex.Message);
    }

    /// <summary>An old, genuinely signed release replayed to walk a user back onto a known-bad build.</summary>
    [Theory]
    [InlineData("2.0.0.0")]   // older
    [InlineData("2.1.0.1")]   // the same version
    public void A_release_that_is_not_newer_is_refused(string offered)
    {
        var path = WriteFile("DataFieldVietnam.exe", "an old client");
        var document = SignDocument("DataFieldVietnam.exe", new Version(offered), path);

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", new Version("2.1.0.1"), PublicKey));
        Assert.Contains("roll back", ex.Message);
    }

    /// <summary>A server predating signing must not be able to talk a client out of checking.</summary>
    [Fact]
    public void An_unsigned_server_is_refused_rather_than_trusted()
    {
        var path = WriteFile("DataFieldVietnam.exe", "whatever it sent");

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, ReleaseSignature.UnknownCommand, "DataFieldVietnam.exe", null, PublicKey));
        Assert.Contains("too old to sign", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("signature not available")]
    [InlineData("DFVSIG1 a.exe 1.0.0.0 abc")]                       // digest too short
    [InlineData("DFVSIG9 a.exe 1.0.0.0 " + SixtyFourHex)]           // unknown format
    [InlineData("DFVSIG1 a.exe notaversion " + SixtyFourHex)]       // bad version
    [InlineData("DFVSIG1 a.exe 1.0.0.0 " + SixtyFourHex)]           // no signature line
    [InlineData("DFVSIG1 a.exe 1.0.0.0 " + SixtyFourHex + "\n!!not base64!!")]
    [InlineData("DFVSIG1 a.exe 1.0.0.0 " + SixtyFourHex + "\nAAAA\nextra")]
    public void Malformed_signature_documents_are_refused(string document)
    {
        var path = WriteFile("a.exe", "contents");

        Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "a.exe", null, PublicKey));
    }

    private const string SixtyFourHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>
    /// The signed bytes are ASCII, and .NET's ASCII encoder replaces anything above U+007F with '?'
    /// rather than refusing -- so two filenames differing only outside ASCII would sign to identical
    /// bytes and one signature would cover both. A signature has to commit to exactly one manifest,
    /// so the parser refuses any name that is not a plain ASCII filename.
    /// </summary>
    [Theory]
    [InlineData("DataFieldVietnamé.exe")]
    [InlineData("DataFieldVietnamÿ.exe")]
    [InlineData("../DataFieldVietnam.exe")]
    [InlineData("sub/DataFieldVietnam.exe")]
    [InlineData(@"sub\DataFieldVietnam.exe")]
    [InlineData("C:DataFieldVietnam.exe")]
    [InlineData("")]
    public void Manifests_naming_anything_but_a_plain_file_are_refused(string fileName)
    {
        Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.ParseManifest($"{ReleaseSignature.Magic} {fileName} 1.0.0.0 {SixtyFourHex}"));
    }

    /// <summary>The two filenames that actually ship have to survive that check.</summary>
    [Theory]
    [InlineData("DataFieldVietnam.exe")]
    [InlineData("DataFieldVietnam_updater.exe")]
    public void Real_release_names_are_accepted(string fileName)
    {
        var manifest = ReleaseSignature.ParseManifest($"{ReleaseSignature.Magic} {fileName} 2.1.0.2 {SixtyFourHex}");

        Assert.Equal(fileName, manifest.FileName);
    }

    /// <summary>
    /// A non-ASCII name can no longer be signed at all, which is what closes the collision: the
    /// encoder would map two distinct names onto one signed line.
    /// </summary>
    [Fact]
    public void Two_names_differing_only_outside_ascii_can_no_longer_both_parse()
    {
        Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.ParseManifest($"{ReleaseSignature.Magic} clienté.exe 1.0.0.0 {SixtyFourHex}"));
        Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.ParseManifest($"{ReleaseSignature.Magic} clientÿ.exe 1.0.0.0 {SixtyFourHex}"));
    }

    /// <summary>A build with no key compiled in must fail shut, never wave the update through.</summary>
    [Fact]
    public void A_build_without_a_pinned_key_refuses_to_verify()
    {
        var path = WriteFile("DataFieldVietnam.exe", "the real client");
        var document = SignDocument("DataFieldVietnam.exe", new Version("2.1.0.2"), path);

        var ex = Assert.Throws<InvalidReleaseSignatureException>(() =>
            ReleaseSignature.VerifyFile(path, document, "DataFieldVietnam.exe", null, publicKeyBase64: ""));
        Assert.Contains("no release signing key", ex.Message);
    }

    /// <summary>Signing covers the exact bytes of the line, so the round-trip has to be exact.</summary>
    [Fact]
    public void Manifest_lines_round_trip_exactly()
    {
        var manifest = new ReleaseSignature.Manifest("DataFieldVietnam.exe", new Version("2.1.0.2"), SixtyFourHex);

        Assert.Equal(manifest, ReleaseSignature.ParseManifest(manifest.ToLine()));
    }
}
