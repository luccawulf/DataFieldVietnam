using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Verifies that an executable served by the update channel was released by the project owner.
/// </summary>
/// <remarks>
/// The update channel hands the client an .exe and then runs it, so whoever can answer that request
/// gets to run code on the player's machine -- and since the client relaunches itself elevated when it
/// lives in an admin-owned directory, quite possibly as administrator. Nothing in the download proves
/// where those bytes came from: the transport is plain TCP with no TLS, the central address is a bare
/// IP, and a game server can redirect the client to an arbitrary host. So the bytes have to prove
/// themselves.
///
/// Each release is signed with an ECDSA P-256 key whose public half is compiled in below. The private
/// half never goes near the file server -- it stays offline on the release machine -- which is the
/// whole point: someone who takes over the server box can serve any file they like, but they cannot
/// produce a signature for it, and the client refuses to run what it cannot verify.
///
/// What is signed is a one-line manifest naming the file, its version and its SHA-256, rather than the
/// raw bytes. Naming and versioning the release is what lets the client refuse a signed updater served
/// in place of a signed client, and refuse a release older than the one already installed.
///
/// Note what that last part does not cover. The rule is "newer than what you have", so a player who is
/// several releases behind can still be walked forward onto an intermediate release rather than the
/// current one -- every check passes, because that release was genuinely signed when it was current.
/// If a release ever turns out to have a hole in it, this scheme cannot retire it; that needs a
/// minimum-version field in a later manifest format, or signatures that age out.
///
/// P-256 because it is in the framework: no third-party crypto to vendor, audit or keep patched.
/// (Ed25519 would be the modern choice but is not in .NET 10's BCL.)
/// </remarks>
public static class ReleaseSignature
{
    /// <summary>Marks the manifest format, so a later scheme can be told apart from this one.</summary>
    public const string Magic = "DFVSIG1";

    /// <summary>What a server replies with when it does not know a command at all.</summary>
    public const string UnknownCommand = "unknown identifier";

    /// <summary>What a server that knows the command replies when it has no signature to give.</summary>
    public const string SignatureUnavailable = "signature not available";

    /// <summary>
    /// The release signing key's public half, as base64 SubjectPublicKeyInfo.
    /// </summary>
    /// <remarks>
    /// Pinned deliberately: there is no certificate authority in this design and no way to rotate a
    /// key remotely. Replacing this value is a code change that ships as a normal signed release, so
    /// an attacker who wants to swap the key has to already be able to ship a signed release.
    ///
    /// An empty value disables verification and is only for a build made before a key exists; it makes
    /// <see cref="IsConfigured"/> false and every Verify call throw rather than silently pass.
    ///
    /// This is the public half and belongs in the repository. The private half lives only on the
    /// release machine, under %APPDATA%\DataFieldVietnam\signing -- see SIGNING.md.
    /// </remarks>
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfTDAOl8M9L+0TVEvQT3WYmCYGX5OQOXTKJUBgKzTmhVp4HOpVVq0he266wWNAyZs001rnxYkHCzE57xmori+lg==";

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKeyBase64);

    /// <summary>One signed release: which file, which version, and the digest of its contents.</summary>
    public record Manifest(string FileName, Version Version, string Sha256Hex)
    {
        /// <summary>The exact bytes that get signed. Must round-trip identically or nothing verifies.</summary>
        public string ToLine() => $"{Magic} {FileName} {Version} {Sha256Hex}";
    }

    /// <summary>Reads back a manifest line, rejecting anything that is not exactly the expected shape.</summary>
    public static Manifest ParseManifest(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new InvalidReleaseSignatureException($"Malformed signature manifest: expected 4 fields, got {parts.Length}.");
        if (parts[0] != Magic)
            throw new InvalidReleaseSignatureException($"Unrecognised signature format '{parts[0]}'. This build is too old to check it.");
        if (!IsPlainFileName(parts[1]))
            throw new InvalidReleaseSignatureException($"Signature manifest names an unacceptable file '{parts[1]}'.");
        if (!Version.TryParse(parts[2], out var version))
            throw new InvalidReleaseSignatureException($"Signature manifest has an unparseable version '{parts[2]}'.");
        if (parts[3].Length != 64 || !IsHex(parts[3]))
            throw new InvalidReleaseSignatureException("Signature manifest does not carry a SHA-256 digest.");

        return new Manifest(parts[1], version, parts[3].ToLowerInvariant());
    }

    /// <summary>
    /// A bare release filename: ASCII letters, digits, dot, dash, underscore, and nothing else.
    /// </summary>
    /// <remarks>
    /// The signed bytes are the manifest line encoded as ASCII, and .NET's ASCII encoder silently
    /// turns anything above U+007F into '?' rather than refusing. Two filenames differing only outside
    /// ASCII would therefore sign to identical bytes, so one signature would verify both -- the
    /// property the whole scheme rests on is that a signature commits to exactly one manifest. Today
    /// the filename is then compared against a compile-time ASCII constant, so nothing can be reached
    /// through the gap, but that is an accident of the call sites rather than a guarantee. Refusing
    /// non-ASCII names here closes it at the parser, where it stays closed no matter who calls.
    ///
    /// Excluding path separators along with it also means a manifest can never name anything but a
    /// file in one place, should the name ever be used to build a path.
    /// </remarks>
    private static bool IsPlainFileName(string name)
    {
        if (name.Length == 0 || name.Length > 128)
            return false;
        foreach (var c in name)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                  || c == '.' || c == '-' || c == '_'))
                return false;
        return true;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    /// <summary>Splits the two-line payload a .sig file (and the updateSig reply) carries.</summary>
    public static (Manifest manifest, byte[] signature) ParseSignatureDocument(string document)
    {
        // Both of these are a server declining to prove itself. Neither is a reason to proceed: an
        // attacker who could talk the client out of checking would have defeated the whole mechanism.
        if (document.Trim() == UnknownCommand)
            throw new InvalidReleaseSignatureException(
                "This server is too old to sign its updates. Refusing to run an unverified executable.");
        if (document.Trim() == SignatureUnavailable)
            throw new InvalidReleaseSignatureException(
                "This server has no signature for the update it offered. Refusing to run an unverified " +
                "executable.");

        var lines = document.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 2)
            throw new InvalidReleaseSignatureException($"Malformed signature document: expected 2 lines, got {lines.Length}.");

        var manifest = ParseManifest(lines[0]);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(lines[1].Trim());
        }
        catch (FormatException)
        {
            throw new InvalidReleaseSignatureException("Signature is not valid base64.");
        }
        return (manifest, signature);
    }

    /// <summary>True when this signature really was made over this manifest by the release key.</summary>
    public static bool VerifyManifest(Manifest manifest, byte[] signature)
    {
        if (!IsConfigured)
            throw new InvalidReleaseSignatureException(
                "This build has no release signing key compiled in, so it cannot check updates.");

        return VerifyManifest(manifest, signature, PublicKeyBase64);
    }

    /// <summary>
    /// As above against a key given explicitly, so the verification path can be tested against a
    /// throwaway key without the pinned one having to exist at compile time.
    /// </summary>
    public static bool VerifyManifest(Manifest manifest, byte[] signature, string publicKeyBase64)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidReleaseSignatureException($"The compiled-in release key is unusable: {ex.Message}");
        }

        // A signature that is not even well-formed is a failed check, not a crash.
        try
        {
            return ecdsa.VerifyData(Encoding.ASCII.GetBytes(manifest.ToLine()), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// The whole check, in the order that matters: the signature has to be genuine before anything it
    /// claims is worth reading, and only then is the file on disk compared against it.
    /// </summary>
    /// <param name="filePath">The downloaded file, already written to disk.</param>
    /// <param name="signatureDocument">The two-line reply from the server.</param>
    /// <param name="expectedFileName">
    /// The name this download is supposed to be. Without this a signed updater could be served in
    /// place of a signed client, or vice versa.
    /// </param>
    /// <param name="mustBeNewerThan">
    /// Refuse a release at or below this version. Pass null when the current version genuinely cannot
    /// be determined -- the signature still holds, only replay protection is given up.
    /// </param>
    public static void VerifyFile(string filePath, string signatureDocument, string expectedFileName, Version? mustBeNewerThan)
        => VerifyFile(filePath, signatureDocument, expectedFileName, mustBeNewerThan, PublicKeyBase64);

    /// <inheritdoc cref="VerifyFile(string, string, string, Version?)"/>
    public static void VerifyFile(
        string filePath, string signatureDocument, string expectedFileName, Version? mustBeNewerThan, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeyBase64))
            throw new InvalidReleaseSignatureException(
                "This build has no release signing key compiled in, so it cannot check updates.");

        var (manifest, signature) = ParseSignatureDocument(signatureDocument);

        if (!VerifyManifest(manifest, signature, publicKeyBase64))
            throw new InvalidReleaseSignatureException(
                "The signature on this update is not valid. The file was not published by the project " +
                "owner, or it was altered on the way here.");

        if (!string.Equals(manifest.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidReleaseSignatureException(
                $"This signature is for '{manifest.FileName}', but the download was '{expectedFileName}'.");

        if (mustBeNewerThan != null && manifest.Version <= mustBeNewerThan)
            throw new InvalidReleaseSignatureException(
                $"The server offered version {manifest.Version}, which is not newer than the installed " +
                $"{mustBeNewerThan}. Refusing to roll back.");

        var actual = ComputeSha256Hex(filePath);
        if (!string.Equals(actual, manifest.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidReleaseSignatureException(
                $"The downloaded file does not match its signature (expected {manifest.Sha256Hex}, got {actual}).");
    }
}

/// <summary>
/// An update could not be shown to be genuine. Always fatal to the update -- never caught and ignored.
/// </summary>
public class InvalidReleaseSignatureException(string message) : Exception(message);
