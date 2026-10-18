using System.Windows;

namespace Comercial.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // "--snapshot <pasta>": gera PNGs das telas com dados de exemplo (sem servidor) e encerra.
        var i = Array.IndexOf(e.Args, "--snapshot");
        if (i >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Snapshot.Run(e.Args.Length > i + 1 ? e.Args[i + 1] : "snapshots");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
