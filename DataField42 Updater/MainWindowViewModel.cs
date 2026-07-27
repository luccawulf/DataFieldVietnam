using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.IO;

namespace DataField42_Updater;

public partial class MainWindowViewModel : ObservableObject
{
    private const string temporaryClientExeName = "DataFieldVietnam_tmp.exe";
    private const string clientExeName = "DataFieldVietnam.exe";

    [ObservableProperty]
    private bool _showPopup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessages))]
    [NotifyPropertyChangedFor(nameof(HasMessagesOrErrors))]
    private string _messages = string.Empty;

    public bool HasMessages => !string.IsNullOrEmpty(Messages);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessages))]
    [NotifyPropertyChangedFor(nameof(HasMessagesOrErrors))]
    private string _errorMessages = string.Empty;

    public bool HasErrorMessages => !string.IsNullOrEmpty(ErrorMessages);

    public bool HasMessagesOrErrors => HasMessages || HasErrorMessages;

    [ObservableProperty]
    private int _percentage;

    public MainWindowViewModel()
    {
        Task.Run(async () => Initialize());
        // Environment.GetCommandLineArgs()
    }

    private async Task Initialize()
    {
        try
        {
            var communication = new DataField42Communication(NullLogger<DataField42Communication>.Instance);
            var backgroundWorker = new DownloadBackgroundWorker();
            backgroundWorker.ProgressChanged += BackgroundWorkerCurrentFile_ProgressChanged;

            communication.StartSession();
            communication.SendString($"updateFile {clientExeName}");
            var fileSize = await communication.ReceiveUlong();
            backgroundWorker.TotalSize = fileSize;

            communication.SendAcknowledgement();

            using (var fileStream = new FileStream(temporaryClientExeName, FileMode.Create, FileAccess.Write))
            {
                await communication.ReceiveFile(fileSize, fileStream, backgroundWorker, CancellationToken.None);
            }
            communication.SendAcknowledgement();

            // Nothing below this point is reversible -- the next step writes over the client the user
            // already has -- so the replacement proves it was published by the project owner first.
            await VerifyDownloadedClient(communication);

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            while (true)
            {
                try
                {
                    File.Move(temporaryClientExeName, clientExeName, overwrite: true);
                    break;
                }
                catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(4))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10));
                }
            }

            ExternalProcess.SwitchTo(clientExeName, arguments: CommandLineArguments.RawString);
        }
        catch (InvalidReleaseSignatureException ex)
        {
            TryDelete(temporaryClientExeName);
            DisplayError(ex.Message);
            DisplayError("Your existing DataField Vietnam has been left alone. Nothing was changed.");
        }
        catch (Exception ex)
        {
            TryDelete(temporaryClientExeName);
            DisplayError($"Can't download DataFieldVietnam.exe: {ex.Message}");
        }
    }

    /// <summary>Checks the downloaded client against the release signature before it replaces anything.</summary>
    /// <remarks>
    /// The installed client's version comes from its file version resource rather than by loading the
    /// assembly, because the published exe is a native apphost.
    ///
    /// Only a genuinely absent client waives the rollback check, and only because there is then no
    /// version to be rolled back to. A client that is present but whose version will not read is
    /// treated as a failure instead: that is the shape an attacker would want, since corrupting the
    /// installed exe would otherwise buy them the right to install any release ever signed.
    /// </remarks>
    private async Task VerifyDownloadedClient(DataField42Communication communication)
    {
        communication.StartSession();
        communication.SendString($"updateSig {clientExeName}");
        var signatureDocument = await communication.ReceiveString();

        Version? installedVersion = null;
        if (File.Exists(clientExeName))
        {
            string? fileVersion;
            try
            {
                fileVersion = FileVersionInfo.GetVersionInfo(Path.GetFullPath(clientExeName)).FileVersion;
            }
            catch (Exception ex)
            {
                throw new InvalidReleaseSignatureException(
                    $"Could not read the version of the installed {clientExeName} ({ex.Message}), so this " +
                    "update cannot be checked for being a downgrade. Nothing was changed.");
            }

            if (fileVersion == null || !Version.TryParse(fileVersion, out var parsed))
                throw new InvalidReleaseSignatureException(
                    $"The installed {clientExeName} does not carry a readable version, so this update " +
                    "cannot be checked for being a downgrade. Nothing was changed.");

            installedVersion = parsed;
        }

        ReleaseSignature.VerifyFile(temporaryClientExeName, signatureDocument, clientExeName, installedVersion);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing runs it either way; the refusal above is what matters.
        }
    }

    public void DisplayMessage(string message)
    {
        message = ">> " + message;
        if (HasMessages)
            message = $"\n{message}";
        Messages += message;
    }

    public void DisplayError(string message)
    {
        message = ">> " + message;
        if (HasErrorMessages)
            message = $"\n{message}";
        ErrorMessages += message;
    }

    private void BackgroundWorkerCurrentFile_ProgressChanged(int percentage)
    {
        Percentage = percentage;
    }
}
