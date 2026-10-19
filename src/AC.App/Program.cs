using System.Diagnostics;
using AC.Infrastructure;

namespace AC.App;

public static class Program
{
    /// <summary>With <see cref="SteamClientCommand.Flag"/> the exe is the short-lived Steam helper and shows no window.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == SteamClientCommand.Flag)
            return SteamClientCommand.Run(args[1..], Console.OpenStandardOutput());

        // Started by an update: the old process still holds the one-instance lock for a moment.
        if (args.Length > 1 && args[0] == UpdateService.WaitFlag && int.TryParse(args[1], out var pid))
            WaitForExit(pid);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}
