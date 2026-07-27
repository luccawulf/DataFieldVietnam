using Force.Crc32;

/// <summary>
/// File digests, with a cache for the ones that only drive sync decisions.
/// </summary>
/// <remarks>
/// Two kinds of caller come through here and they need different things.
///
/// Deciding whether a file already matches what a server is offering is a hot path -- a sync inspects
/// the whole mod folder, and re-reading gigabytes to answer "do I already have this?" would make the
/// client feel broken. Those callers get <see cref="Crc32CWithCache"/>, which remembers a digest
/// against the file's identity and skips the read.
///
/// Checking that a file which just arrived over the network is the file that was promised is not that.
/// It has to read the bytes, every time, or it is not a check at all -- so it calls
/// <see cref="Crc32C"/>, which never consults the cache. Keeping the two apart is deliberate: the
/// cached form used to be reachable from the verification path, and because the cache was keyed on
/// nothing but size and last-modified time -- both of which the server itself supplies, and which the
/// client stamps onto the download before verifying it -- a server could line those up with a record
/// the client already held and have its file "verified" without a single byte being read.
/// </remarks>
public static class CheckSums
{
    private static ChecksumRepository _checksumCacheRepository = new("DataFieldVietnam/ChecksumCache.yaml");

    public static uint Crc32(string filePath) => Crc32Algorithm.Compute(File.ReadAllBytes(filePath));

    /// <summary>Reads the file and digests it. Never cached -- this is the one verification uses.</summary>
    /// <remarks>
    /// Streamed in blocks rather than through File.ReadAllBytes: the archives here run to hundreds of
    /// megabytes (one sound.rfa is over 400 MB) and reading one whole puts a single allocation of that
    /// size on the large object heap. Feeding the digest in chunks measures faster as well.
    /// </remarks>
    public static uint Crc32C(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: false);

        var buffer = new byte[1 << 20];
        uint checksum = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            checksum = Crc32CAlgorithm.Append(checksum, buffer, 0, read);

        return checksum;
    }

    /// <summary>Reads the file and returns its SHA-256 as lowercase hex. Never cached.</summary>
    /// <remarks>
    /// Used to check a freshly downloaded file, where the bytes have to be read anyway. It is not a
    /// stronger guarantee about the server -- the digest it is compared against arrives from that same
    /// server over the same connection, so a server serving something else simply sends the matching
    /// digest. What it does give is a check that cannot be satisfied by accident: CRC32 is a linear
    /// code, so any file can be adjusted in four bytes to land on a chosen value, which makes it a
    /// poor answer to "are these the bytes that were promised" even before anyone is being hostile.
    /// </remarks>
    public static string Sha256(string filePath)
    {
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: false);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// The digest of a file that is already on disk and is not being verified -- may come from cache.
    /// </summary>
    /// <remarks>
    /// Do not call this to check something that arrived over the network. See the note on the class.
    /// </remarks>
    public static uint Crc32CWithCache(string filePath)
    {
        var fileSize = new System.IO.FileInfo(filePath).Length;
        var fileLastTimeModified = ((DateTimeOffset)File.GetLastWriteTime(filePath)).ToUnixTimeSeconds();

        // The path is part of the identity. Without it any two files sharing a size and a timestamp
        // are treated as the same file, which happens innocently all the time -- a mod's archives are
        // typically packed in one go and unpack with identical timestamps.
        var (checksumFound, checksumString) = _checksumCacheRepository.FindChecksum(
            filePath, fileSize, (ulong)fileLastTimeModified);

        if (checksumFound && uint.TryParse(checksumString, out var cached))
            return cached;

        var checksum = Crc32C(filePath);
        _checksumCacheRepository.AddRecord(filePath, checksum.ToString(), fileSize, (ulong)fileLastTimeModified);
        return checksum;
    }

    /// <summary>
    /// Writes the digest cache to disk. Call once after a batch of lookups, not per file.
    /// </summary>
    /// <remarks>
    /// Losing this to a crash costs nothing but a recompute next time, which is why it is worth
    /// batching: saving on every record rewrote the whole file each time and grew quadratically, to
    /// the point where bookkeeping cost more than the hashing it was avoiding.
    /// </remarks>
    public static void FlushCache() => _checksumCacheRepository.Save();
}
