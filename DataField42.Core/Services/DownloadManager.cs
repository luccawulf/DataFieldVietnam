using Microsoft.Extensions.Logging;

public class DownloadManager
{
    private readonly DataField42Communication _communication;
    private readonly DownloadDecisionMaker _downloadDecisionMaker;
    private readonly ILocalFileCacheManager _localFileCacheManager;
    private readonly ILogger<DownloadManager> _logger;

    private string? _mod;
    private string? _map;
    private List<FileInfo>? _fileInfos;

    /// <summary>The mod being synced: the one requested, unless the server's offer corrected it.</summary>
    public string? Mod => _mod;

    public DownloadManager(
        DataField42Communication dataField42Communication,
        DownloadDecisionMaker downloadDecisionMaker,
        ILocalFileCacheManager localFileCacheManager,
        ILogger<DownloadManager> logger)
    {
        _communication = dataField42Communication;
        _downloadDecisionMaker = downloadDecisionMaker;
        _localFileCacheManager = localFileCacheManager;
        _logger = logger;
    }

    /// <summary>Step 1 in synchronizing files.</summary>
    public async Task<IEnumerable<FileInfo>> DownloadFilesRequest(string mod, string map, string ip, int port, string keyHash, CancellationToken cancellationToken)
    {
        _mod = mod;
        _map = map;

        _logger.LogInformation($"Requesting file list for mod={mod}, map={map} from {ip}:{port}.");

        _localFileCacheManager.RemoveWorkingDirectory();
        _communication.StartSession();
        _communication.SendString($"download {map} {mod} {ip} {port} {keyHash}");

        _fileInfos = await _communication.ReceiveFileInfos(cancellationToken);
        _logger.LogDebug($"Received {_fileInfos.Count} file infos from server.");

        AdoptModFromOffer();

        // TODO: better messaging for double files in list 
        // TODO: check for absense of base rfa

        if (_fileInfos.Count > 100) // TODO: check a resonable max
            throw new Exception($"Server wants to sync {_fileInfos.Count} files which is more than 100");

        await _downloadDecisionMaker.CheckDownloadRequests(_fileInfos, cancellationToken);

        // The decision pass is what populates the digest cache; write it once here rather than on
        // every record.
        CheckSums.FlushCache();

        var toDownload = _fileInfos.Count(x => x.SyncType == SyncType.Download);
        var fromCache = _fileInfos.Count(x => x.SyncType == SyncType.LocalFileCache);
        var local = _fileInfos.Count(x => x.SyncType == SyncType.LocalFile);
        var skipped = _fileInfos.Count(x => x.SyncType == SyncType.None);
        _logger.LogInformation($"Download decision: {toDownload} to download, {fromCache} from cache, {local} already local, {skipped} skipped.");

        return _fileInfos;
    }

    /// <summary>
    /// Takes the mod from the offer when the server answered for a different one than was asked for.
    /// </summary>
    /// <remarks>
    /// A Battlefield Vietnam client cannot know what mod a server runs -- its browser never parses
    /// game_id -- so someone sitting in base BFVietnam who joins a DiceCity_V server asks for
    /// BFVietnam. The server corrects that from the map and answers with the mod that ships it.
    /// Without adopting the correction VerifyFileList would reject a perfectly good offer, and the
    /// game would be relaunched on the very mod it could not join with.
    /// </remarks>
    private void AdoptModFromOffer()
    {
        if (_fileInfos == null || _mod == null || _map == null || _map == "*")
            return;

        var levelOfRequestedMap = _fileInfos.FirstOrDefault(x =>
            x.FileType == Bf1942FileType.Level &&
            Path.GetFileNameWithoutExtension(x.FileNameWithoutPatchNumber).ToLower() == _map.ToLower());

        if (levelOfRequestedMap == null || levelOfRequestedMap.Mod.ToLower() == _mod.ToLower())
            return;

        _logger.LogInformation($"Server answered for mod {levelOfRequestedMap.Mod}, not the requested {_mod}; adopting it.");
        _mod = levelOfRequestedMap.Mod;
    }

    /// <summary>Step 2 in synchronizing files.</summary>
    /// <returns>(hasMod, hasMap)</returns>
    public (bool, bool) VerifyFileList()
    {
        if (_fileInfos == null)
            throw new ArgumentNullException($"{nameof(_fileInfos)} is null, make sure to run {nameof(DownloadFilesRequest)} first");
        if (_mod == null)
            throw new ArgumentNullException($"{nameof(_mod)} is null, make sure to run {nameof(DownloadFilesRequest)} first");
        if (_map == null)
            throw new ArgumentNullException($"{nameof(_map)} is null, make sure to run {nameof(DownloadFilesRequest)} first");

        var hasMod = false;
        var hasMap = false;

        foreach (var fileInfo in _fileInfos)
        {
            if (fileInfo.Mod.ToLower() == _mod.ToLower())
            {
                hasMod = true;
                if (fileInfo.FileType == Bf1942FileType.Level && Path.GetFileNameWithoutExtension(fileInfo.FileNameWithoutPatchNumber).ToLower() == _map.ToLower())
                    hasMap = true;
            }
        }

        _logger.LogDebug($"VerifyFileList — hasMod={hasMod}, hasMap={hasMap}.");
        return (hasMod, hasMap);
    }

    /// <summary>Step 3 in synchronizing files.</summary>
    public async Task DownloadFilesDownload(DownloadBackgroundWorker backgroundWorkerTotal, DownloadBackgroundWorker backgroundWorkerCurrentFile, CancellationToken cancellationToken)
    {
        // TODO: add to protocol a way to tell the client that it does not have mod or map, maybe dont do this in download manager but in a new manager (downloadCheck)

        if (_fileInfos == null)
            throw new ArgumentNullException($"{nameof(_fileInfos)} is null, make sure to run {nameof(DownloadFilesRequest)} first");

        List<DownloadBackgroundWorker> backgroundWorkers = new() { backgroundWorkerTotal, backgroundWorkerCurrentFile };

        var fileInfosOfFilesToDownload = _fileInfos.Where(x => x.SyncType == SyncType.Download);
        var numberOfFilesExpected = fileInfosOfFilesToDownload.Count();
        var totalSizeExpected = fileInfosOfFilesToDownload.Sum(x => x.Size);

        _logger.LogInformation($"Starting download of {numberOfFilesExpected} files, total size {totalSizeExpected} bytes.");

        var responses = new List<string>();
        foreach (var fileInfo in _fileInfos)
            responses.Add(fileInfo.SyncType == SyncType.Download ? "yes" : "no");

        _communication.SendString(string.Join(' ', responses));

        var data = await _communication.ReceiveSpaceSeperatedString(3); // acknowledgement numberOfFiles totalSize

        var numberOfFiles = int.Parse(data.ElementAt(1));
        if (numberOfFiles != numberOfFilesExpected)
            throw new Exception($"numberOfFiles: {numberOfFiles} != {numberOfFilesExpected}");

        var totalSize = ulong.Parse(data.ElementAt(2));
        if (totalSize != totalSizeExpected)
            throw new Exception($"totalSize: {totalSize} != {totalSizeExpected}");

        _communication.SendAcknowledgement();

        int fileIndex = 0;
        foreach (var fileInfoOfFileToDownload in fileInfosOfFilesToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileIndex++;

            var fileInfo = await _communication.ReceiveFileInfo();
            if (!fileInfo.IsEqualTo(fileInfoOfFileToDownload))
                throw new Exception("File info sent right before file download does not match the agreed file info sequence");

            _logger.LogDebug($"Downloading file {fileIndex}/{numberOfFilesExpected}: {fileInfo.FilePath} ({fileInfo.Size} bytes).");

            backgroundWorkerCurrentFile.TotalSize = fileInfo.Size;
            _communication.SendAcknowledgement();
            var filePath = _localFileCacheManager.GetWorkingDirectoryFilePath(fileInfo);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "");

            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            await _communication.ReceiveFile(fileInfo.Size, fileStream, backgroundWorkers, cancellationToken);

            _communication.SendAcknowledgement();
            fileStream.Close();
            FileHelper.SetLastWriteTime(filePath, fileInfo.LastModifiedTimestamp);

            _logger.LogDebug($"File {fileInfo.FilePath} downloaded and saved.");
        }

        _communication.SendAcknowledgement();
        _communication.Dispose();
        _logger.LogInformation($"All {numberOfFilesExpected} files downloaded successfully.");
    }

    /// <summary>Step 4 in synchronizing files.</summary>
    public void DownloadFilesWrapUp()
    {
        if (_fileInfos == null)
            throw new ArgumentNullException($"{nameof(_fileInfos)} is null, make sure to run {nameof(DownloadFilesRequest)} first");

        _logger.LogDebug("Verifying checksums and sizes of downloaded files.");

        var fileInfosOfFilesToDownload = _fileInfos.Where(x => x.SyncType == SyncType.Download);

        // Check every downloaded file against what was promised, by reading it.
        //
        // The digest is computed here rather than by building a FileInfo, which is what this used to
        // do. FileInfo takes the cached route, and that cache was keyed on size and last-modified time
        // -- both of which the server declares, and the second of which is stamped onto the file at
        // ReceiveFile time from the server's own value, before this runs. A server could therefore
        // name a size and a timestamp matching a record the client already held and have its file pass
        // this check without a byte of it being read. Hashing directly is the fix; the size comparison
        // is kept because a truncated transfer should say so plainly rather than as a digest mismatch.
        foreach (var fileInfoOfFileToDownload in fileInfosOfFilesToDownload)
        {
            var filePathInWorkingDirectory = $"{_localFileCacheManager.WorkingDirectoryWithSlash}mods/{fileInfoOfFileToDownload.Mod}/{fileInfoOfFileToDownload.FilePath}";

            var actualSize = (ulong)new System.IO.FileInfo(filePathInWorkingDirectory).Length;
            if (actualSize != fileInfoOfFileToDownload.Size)
                throw new Exception($"Downloaded file has incorrect size: {actualSize}. Expected: {fileInfoOfFileToDownload.Size}");

            // SHA-256 when the server offered one, CRC32C otherwise -- servers older than the second
            // digest send five fields and leave it empty. Only one of the two is computed, so nothing
            // is read twice.
            if (fileInfoOfFileToDownload.Sha256 != "")
            {
                var actualSha256 = CheckSums.Sha256(filePathInWorkingDirectory);
                if (actualSha256 != fileInfoOfFileToDownload.Sha256)
                    throw new Exception($"Downloaded file has incorrect SHA-256: {actualSha256}. Expected: {fileInfoOfFileToDownload.Sha256}");
            }
            else
            {
                var actualChecksum = CheckSums.Crc32C(filePathInWorkingDirectory).ToString("X8");
                if (actualChecksum != fileInfoOfFileToDownload.Checksum)
                    throw new Exception($"Downloaded file has incorrect checksum: {actualChecksum}. Expected: {fileInfoOfFileToDownload.Checksum}");
            }
        }

        _logger.LogDebug("Checksums verified. Moving files from cache to working directory.");
        var fileInfosOfFilesInCache = _fileInfos.Where(x => x.SyncType == SyncType.LocalFileCache);
        _localFileCacheManager.MoveFilesFromCacheToWorkingDirectory(fileInfosOfFilesInCache);

        _logger.LogDebug("Moving files from game to cache directory.");
        var fileInfoGroups = FileInfoGroup.GetFileInfoGroups(_fileInfos);
        _localFileCacheManager.MoveFilesFromGameToCacheDirectory(fileInfoGroups);

        _logger.LogDebug("Moving files from working directory to game directory.");
        _localFileCacheManager.EmptyWorkingDirectoryIntoGameDirectory();
        _localFileCacheManager.RemoveWorkingDirectory();

        _logger.LogInformation("File sync wrap-up complete.");
    }
}
