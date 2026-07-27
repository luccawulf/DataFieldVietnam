using Microsoft.Extensions.Logging;
using System.Reflection;

public class UpdateManager
{
    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version;

    private readonly DataField42Communication _communication;
    private readonly ILogger<UpdateManager> _logger;

    // Public so the client can clean this up: after an update the updater relaunches the client and
    // exits, leaving the exe it was downloaded as sitting in the game folder.
    public const string UpdaterFileName = "DataFieldVietnam_updater.exe";

    public UpdateManager(DataField42Communication communication, ILogger<UpdateManager> logger)
    {
        _communication = communication;
        _logger = logger;
    }

    /// <remarks>
    /// This method ends by running an executable that arrived over the network, so everything between
    /// the download and that call is what stops a hostile server -- or anyone who can answer for one,
    /// since the transport has no TLS and a game server may redirect the client elsewhere -- from
    /// running code as the player. The signature is fetched and checked before the updater is
    /// launched, and a file that fails is deleted rather than left on disk for something else to pick
    /// up later.
    /// </remarks>
    public async Task Update(DownloadBackgroundWorker backgroundWorker, CancellationToken cancellationToken, string restartArguments)
    {
        _logger.LogInformation($"Starting update from version {Version}.");
        _communication.StartSession();
        _communication.SendString($"update {Version}");
        var fileSize = await _communication.ReceiveUlong();
        backgroundWorker.TotalSize = fileSize;
        _logger.LogDebug($"Updater file size: {fileSize} bytes.");

        _communication.SendAcknowledgement();

        using (var fileStream = new FileStream(UpdaterFileName, FileMode.Create, FileAccess.Write))
        {
            await _communication.ReceiveFile(fileSize, fileStream, backgroundWorker, cancellationToken);
        }
        _communication.SendAcknowledgement();

        await VerifyDownloadedUpdater();

        _logger.LogInformation($"Update download complete and verified. Launching updater with args: {restartArguments}.");
        ExternalProcess.SwitchTo(UpdaterFileName, arguments: restartArguments);
    }

    /// <summary>Fetches the release signature for the updater just downloaded, and checks it.</summary>
    /// <remarks>
    /// The updater bootstrap carries the version of the release it belongs to, so requiring it to be
    /// newer than this client also rules out an old signed updater being replayed to walk a player
    /// back onto a build with a known hole in it.
    /// </remarks>
    private async Task VerifyDownloadedUpdater()
    {
        try
        {
            _communication.StartSession();
            _communication.SendString($"updateSig {UpdaterFileName}");
            var signatureDocument = await _communication.ReceiveString();

            ReleaseSignature.VerifyFile(UpdaterFileName, signatureDocument, UpdaterFileName, mustBeNewerThan: Version);
            _logger.LogInformation($"Updater signature verified against the pinned release key.");
        }
        catch (Exception ex)
        {
            TryDelete(UpdaterFileName);
            _logger.LogError($"Refusing to run the downloaded updater: {ex.Message}");

            throw ex is InvalidReleaseSignatureException
                ? ex
                : new InvalidReleaseSignatureException(
                    $"The update could not be verified as genuine, so it was discarded: {ex.Message}");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // Worth knowing about, but the refusal to launch it is what actually protects the user.
            _logger.LogWarning($"Could not delete the rejected update at {path}: {ex.Message}");
        }
    }

    public async Task<Version> RequestVersion()
    {
        _logger.LogDebug("Requesting server version.");
        (_, var version) = await _communication.HandShake(Version);
        _logger.LogDebug($"Server version: {version}.");
        return version;
    }
}
