using YamlDotNet.Serialization;

/// <summary>
/// Remembers file digests so a sync does not have to re-read the whole mod folder every time.
/// </summary>
/// <remarks>
/// A record is only a valid answer for the same file, so the path is part of the key alongside size
/// and last-modified time. It used to be keyed on size and time alone, which collides constantly in
/// practice -- the archives of a mod are usually packed together and land with identical timestamps --
/// and meant one file's digest could be handed back for another.
///
/// Records written by that older scheme have no path, so they simply never match and are recomputed
/// once. That is the intended upgrade path: no migration, no version field, and no chance of an old
/// record being trusted under the new rules.
///
/// This is a cache for sync decisions. Being wrong here costs a needless download or a needless skip,
/// never a security decision -- see the note on <see cref="CheckSums"/> for why verification does not
/// come through here.
/// </remarks>
public class ChecksumRepository
{
    private readonly string _filename;
    private readonly Dictionary<string, ChecksumRecord> _records;
    private readonly object lockObj = new();
    private readonly IDeserializer deserializer = new DeserializerBuilder().Build();
    private readonly ISerializer serializer = new SerializerBuilder().Build();

    /// <summary>Set when the in-memory records differ from the file, so a save is worth doing.</summary>
    private bool _dirty;

    public ChecksumRepository(string filename)
    {
        _filename = filename;
        _records = LoadRecords();
    }

    private Dictionary<string, ChecksumRecord> LoadRecords()
    {
        var records = new Dictionary<string, ChecksumRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var loaded = deserializer.Deserialize<List<ChecksumRecord>>(File.ReadAllText(_filename));
            if (loaded == null)
                return records;

            foreach (var record in loaded)
            {
                // Records from the older path-less format can never be matched again; drop them on
                // load rather than carrying them forward forever.
                if (string.IsNullOrEmpty(record.Path))
                    continue;
                records[KeyOf(record.Path, record.Size, record.LastTimeModified)] = record;
            }
        }
        catch
        {
            // A missing or unreadable cache is not a failure: everything is recomputed.
        }
        return records;
    }

    private static string KeyOf(string path, long size, ulong lastTimeModified)
        => $"{Path.GetFullPath(path)}|{size}|{lastTimeModified}";

    /// <summary>
    /// Writes the cache out. Called once at the end of a batch rather than per record.
    /// </summary>
    /// <remarks>
    /// Serialising the whole file on every single add made this quadratic, and at a few hundred
    /// records it cost more time than the hashing it was there to avoid.
    /// </remarks>
    public void Save()
    {
        lock (lockObj)
        {
            if (!_dirty)
                return;
            FileHelper.WriteText(_filename, serializer.Serialize(_records.Values.ToList()));
            _dirty = false;
        }
    }

    public void AddRecord(string path, string checksum, long size, ulong lastTimeModified)
    {
        lock (lockObj)
        {
            _records[KeyOf(path, size, lastTimeModified)] =
                new ChecksumRecord(path, checksum, size, lastTimeModified);
            _dirty = true;

            // Kept bounded: this grows with every file ever inspected, and an unbounded YAML file that
            // is rewritten wholesale would eventually become the slow part of a sync.
            if (_records.Count > MaximumRecords)
                TrimOldest();
        }
    }

    private const int MaximumRecords = 5000;

    private void TrimOldest()
    {
        foreach (var key in _records.OrderBy(x => x.Value.LastTimeModified)
                                    .Take(_records.Count - MaximumRecords / 2)
                                    .Select(x => x.Key)
                                    .ToList())
            _records.Remove(key);
    }

    public (bool, string) FindChecksum(string path, long size, ulong lastTimeModified)
    {
        lock (lockObj)
        {
            return _records.TryGetValue(KeyOf(path, size, lastTimeModified), out var record)
                   && !string.IsNullOrEmpty(record.Checksum)
                ? (true, record.Checksum)
                : (false, "");
        }
    }

    private struct ChecksumRecord
    {
        public string Path { get; set; }
        public string Checksum { get; set; }
        public long Size { get; set; }
        public ulong LastTimeModified { get; set; }

        public ChecksumRecord(string path, string checksum, long size, ulong lastTimeModified)
        {
            Path = path;
            Checksum = checksum;
            Size = size;
            LastTimeModified = lastTimeModified;
        }
    }
}
