using DataField42.Settings;
using DataField42.ViewModels;
using DataField42.Views;
using Microsoft.Extensions.Logging;
using Serilog;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace DataField42;

public partial class App : Application
{
#pragma warning disable CS8618
    private Microsoft.Extensions.Logging.ILogger _logger;
#pragma warning restore CS8618

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsAdministrator() && IsInAdminDirectory())
            ExternalProcess.SwitchTo(Environment.ProcessPath, adminMode: true);

        // Startup logger after admin mode has atained (if needed).
        SetupSerilog();
        var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(dispose: true));
        _logger = loggerFactory.CreateLogger<App>();

        _logger.LogInformation($"Application starting. Version: {UpdateManager.Version}.");

        var settingsService = new SettingsService("DataFieldVietnam/Settings.ini");
        var bf1942Client = new Bf1942Client("BfVietnam.exe");
        var mainWindowViewModel = new MainWindowViewModel(settingsService, bf1942Client, loggerFactory);

        var mainWindow = new MainWindow(mainWindowViewModel);
        mainWindow.Show();
        MainWindow = mainWindow;

        Task.Run(CleanUpUpdateLeftovers);
        Task.Run(() => RunStartupUpdate(loggerFactory));
    }

    /// <summary>
    /// Deletes the bootstrap the updater left behind after a self-update. The updater downloads itself
    /// into the game folder, swaps the client, relaunches it, and exits — so its ~54 MB exe (and any
    /// stray temp) is only ever present as an update artifact, safe to remove here.
    /// </summary>
    private static void CleanUpUpdateLeftovers()
    {
        // The updater exits at almost the same moment we start, so it can still hold its file for a
        // beat. Retry briefly; if it is somehow still locked, the next launch clears it. Best-effort —
        // never let cleanup interfere with startup.
        try
        {
            // Environment.ProcessPath is the real exe location even for a single-file publish, where
            // AppContext.BaseDirectory would point at the extraction folder instead.
            var gameFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            foreach (var name in new[] { UpdateManager.UpdaterFileName, "DataFieldVietnam_tmp.exe" })
            {
                var path = Path.Combine(gameFolder, name);
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        break;
                    }
                    catch (IOException) { System.Threading.Thread.Sleep(50); }
                    catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50); }
                }
            }
        }
        catch { /* cleanup is best-effort */ }
    }

    private static void SetupSerilog()
    {
        Directory.CreateDirectory("DataFieldVietnam/Logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: "DataFieldVietnam/Logs/DataFieldVietnam.log",
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u4} {SourceContext}: {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    private async Task RunStartupUpdate(ILoggerFactory loggerFactory)
    {
        _logger.LogDebug("Checking for application updates.");
        try
        {
            var communication = new DataField42Communication(loggerFactory.CreateLogger<DataField42Communication>());
            var updateManager = new UpdateManager(communication, loggerFactory.CreateLogger<UpdateManager>());
            var masterVersion = await updateManager.RequestVersion();

            if (masterVersion > UpdateManager.Version)
            {
                _logger.LogInformation($"Update available: {masterVersion}. Downloading.");
                var backgroundWorker = new DownloadBackgroundWorker();
                await updateManager.Update(backgroundWorker, CancellationToken.None, CommandLineArguments.RawString ?? "");
            }
            else
            {
                _logger.LogDebug("Application is up to date.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup update check failed silently.");
        }
    }

    private static bool IsAdministrator() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static bool IsInAdminDirectory()
    {
        const string testFileName = "DataFieldVietnam - admin test";
        try
        {
            File.WriteAllText(testFileName, "This is a test.");
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            File.Delete(testFileName);
        }
        return false;
    }
}
