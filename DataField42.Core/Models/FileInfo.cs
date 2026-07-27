using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

public class FileInfo
{
    public string Mod { get; set; }
    public string FilePath { get; set; }
    public string Checksum { get; set; }
    public ulong Size { get; set; }
    public ulong LastModifiedTimestamp { get; set; }
    public Bf1942FileType FileType { get; set; } = Bf1942FileType.None;

    public SyncType SyncType { get; set; } = SyncType.Unknown;

    public string Directory => Path.GetDirectoryName(FilePath) ?? "";
    public string FileName => Path.GetFileName(FilePath);
    public bool RepresentsAbsenceOfFile => Size == 0;

    public string FileNameWithoutPatchNumber
    {
        get
        {
            if (FileType == Bf1942FileType.Level || FileType == Bf1942FileType.Archive)
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(FileName);
                var fileExtension = Path.GetExtension(FileName);
                var match = Regex.Match(fileNameWithoutExtension, $"^([{AllowableChars}]+)(_{{1}})([0-9]+)$");
                return match.Success ? $"{match.Groups[1].Value}{fileExtension}" : FileName;
            }
            else
            {
                return FileName;
            }
        }
    }

    public const string AllowableChars = @"0-9a-zA-Z_-";

    public FileInfo(string localFilePath, string gamePath, bool fast = false, bool fromCache = false)
    {
        var pathPartsBase = gamePath.Replace('\\', '/').Split("/", StringSplitOptions.RemoveEmptyEntries);
        var pathPartsFile = localFilePath.Replace('\\', '/').Split("/");

        if (pathPartsBase.Length >= pathPartsFile.Length)
            throw new ArgumentException($"{gamePath} is not the base of {localFilePath}");

        for (int i = 0; i < pathPartsBase.Length; i++)
            if (pathPartsBase[i] != pathPartsFile[i])
                throw new ArgumentException($"{gamePath} is not the base of {localFilePath}");

        var pathPartsFileRelative = pathPartsFile[(pathPartsBase.Length + 2)..];
        var relativePath = string.Join("/", pathPartsFileRelative);
        if (fromCache) // remove hash from name
        {
            var directory = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            var correctFileName = Path.GetFileName(relativePath).Split(' ', 2)[1];
            relativePath = $"{directory}{(directory == "" ? "" : "/")}{correctFileName}";
        }
            

        var mod = pathPartsFile[pathPartsBase.Length + 1];

        var crc32 = fast ? "" : CheckSums.Crc32CWithCache(localFilePath).ToString("X8");
        string size = fast ? "" : (new System.IO.FileInfo(localFilePath).Length).ToString();
        string lastModifiedTimestamp = fast ? "" : ((DateTimeOffset)File.GetLastWriteTime(localFilePath)).ToUnixTimeSeconds().ToString();

        ParseArguments(mod, relativePath, crc32, size, lastModifiedTimestamp, fast);
    }

    /// <remarks>
    /// A sixth field, when the server sends one, is the file's SHA-256. Servers that predate it send
    /// five and <see cref="Sha256"/> stays empty, which is why it is read defensively rather than by
    /// position: the same client has to work against both.
    /// </remarks>
    public FileInfo(IEnumerable<string> spaceSeperatedString)
    {
        ParseArguments(spaceSeperatedString.ElementAt(0), spaceSeperatedString.ElementAt(1), spaceSeperatedString.ElementAt(2), spaceSeperatedString.ElementAt(3), spaceSeperatedString.ElementAt(4), checkSafety: true);

        if (spaceSeperatedString.Count() > 5)
            Sha256 = NormaliseSha256(spaceSeperatedString.ElementAt(5));
    }

    /// <summary>
    /// The file's SHA-256 as lowercase hex, or empty when the server did not send one.
    /// </summary>
    /// <remarks>
    /// Only ever used to check a file that has just been downloaded. Sync decisions stay on the CRC32C
    /// in <see cref="Checksum"/>, which is cached against files already on disk -- so adding this
    /// costs one hash of the bytes that were just read, and nothing on the common path where a sync
    /// decides everything is already present.
    /// </remarks>
    public string Sha256 { get; set; } = "";

    /// <summary>Accepts a well-formed digest and ignores anything else, rather than trusting it.</summary>
    private static string NormaliseSha256(string value)
    {
        if (value.Length != 64)
            return "";
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return "";
        return value.ToLowerInvariant();
    }

    [MemberNotNull(nameof(Mod))]
    [MemberNotNull(nameof(FilePath))]
    [MemberNotNull(nameof(Checksum))]
    public void ParseArguments(string mod, string filePath, string crc32, string size, string lastModifiedTimestamp, bool fast = false, bool checkSafety = false)
    {
        Mod = mod;
        FilePath = Regex.Replace(filePath, "^\"|\"$", ""); //remove quotes around string
        Checksum = fast ? "" : crc32;
        Size = fast ? 0 : ulong.Parse(size);
        LastModifiedTimestamp = fast ? 0 : ulong.Parse(lastModifiedTimestamp);

        if (checkSafety && !Regex.IsMatch(Mod, $"^[{AllowableChars}]*$"))
            throw new Exception($"Mod name contains illegal characters: {Mod}");

        //TODO Parse/validate Crc32 & Size & LastModifiedTimestamp, except if fast

        // parse FilePath:
        List<Tuple<string, Bf1942FileType>> fileTypeFolderLocations = new()
        {   // mind the order!
            new("movies/", Bf1942FileType.Movie),
            new("music/", Bf1942FileType.Music),
            new("archives/bfvietnam/levels/", Bf1942FileType.Level),
            new("archives/bfvietnam/", Bf1942FileType.Archive),
            new("archives/", Bf1942FileType.Archive),
            new("", Bf1942FileType.ModMiscFile),
        };
        // Anything not on this list is rejected outright, so a file the server offers but the client
        // does not know about takes the whole sync down rather than being skipped.
        List<string> modMiscFileNames = new()
        {
            "contentcrc32.con",
            "init.con",
            "levelcheck.con",  // Battlefield Vietnam's per-mod archive-hash manifest
            "mod.dll",
            "lexiconall.dat",
            "serverinfo.dds",
            "bfdist.vlu",
        };

        // TODO: allow only certain archives? and only one level?

        var filePathLower = FilePath.ToLower();
        var fileName = "";
        foreach((var fileTypeFolderLocation,var fileType) in fileTypeFolderLocations)
        {
            if (filePathLower.StartsWith(fileTypeFolderLocation))
            {
                FileType = fileType;
                fileName = filePathLower[fileTypeFolderLocation.Length..];
                break;
            }

        }
        if (FileType == Bf1942FileType.None)
            throw new Exception($"Can't determine file type: {filePathLower}");

        var fileExtensionLower = Path.GetExtension(fileName).ToLower();
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        switch (FileType)
        {
            case Bf1942FileType.Movie:
            case Bf1942FileType.Music:
                if (fileExtensionLower != ".bik")
                    throw new Exception($"Illegal file extension for {filePathLower}");
                break;
            case Bf1942FileType.ModMiscFile:
                if (!modMiscFileNames.Contains(fileName.ToLower()))
                    throw new Exception($"Illegal file: {filePathLower}");
                break;
            case Bf1942FileType.Archive:
            case Bf1942FileType.Level:
                if (fileExtensionLower != ".rfa")
                    throw new Exception($"Illegal file extension for {filePathLower}");
                break;
        }

        if (checkSafety && !Regex.IsMatch(fileNameWithoutExtension, $"^[{AllowableChars}]*$"))
            throw new Exception($"File name contains illegal characters: {filePath}");
    }

    public override string ToString() => $"{Mod} \"{FilePath}\" {Checksum} {Size.ToReadableFileSize()} {LastModifiedTimestamp}";

    
    public bool IsEqualTo(FileInfo fileInfo)
    {
        return Mod.ToLower() == fileInfo.Mod.ToLower()
            && FilePath.ToLower() == fileInfo.FilePath.ToLower()
            && Checksum == fileInfo.Checksum
            && Size == fileInfo.Size;
    }
}
