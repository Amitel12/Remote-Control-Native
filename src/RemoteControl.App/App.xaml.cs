using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vortice.MediaFoundation;

namespace RemoteControl.App;

public partial class App : Application
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteControl", "log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show(
                $"Remote Control hit an unexpected error and needs to close.\n\n{args.Exception.Message}\n\nDetails were written to:\n{LogPath}",
                "Remote Control", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                WriteCrashLog(ex);
        };

        // Media Foundation is started once for the process and deliberately never shut down here:
        // MftProbe.Enumerate pairs its own MFStartup with an unconditional MFShutdown, which was
        // found to leave the DXVA/hardware subsystem torn down for whatever ran next in the same
        // process (see tools/LoopbackHarness/Program.cs's comment on this). A GUI can start and
        // stop many sessions across its lifetime, so the safe rule is: start once, never stop --
        // process exit reclaims it.
        MediaFactory.MFStartup(false).CheckError();
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.UtcNow:O}] Unhandled exception:\n{ex}\n\n");
        }
        catch
        {
            // Logging the crash must never itself crash the crash handler.
        }
    }
}
