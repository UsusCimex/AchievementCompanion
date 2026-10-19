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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
