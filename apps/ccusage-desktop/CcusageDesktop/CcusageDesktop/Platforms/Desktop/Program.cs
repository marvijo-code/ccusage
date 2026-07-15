using Uno.UI.Hosting;

namespace CcusageDesktop;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // e.g. `CcusageDesktop.exe --tab Blocks` opens on that tab (also used by UI verification).
        var tabIndex = Array.IndexOf(args, "--tab");
        if (tabIndex >= 0 && tabIndex + 1 < args.Length) MainPage.InitialTab = args[tabIndex + 1];
        if (Array.IndexOf(args, "--no-refresh") >= 0) MainPage.AutoRefreshDisabled = true;
        if (Array.IndexOf(args, "--expanded") >= 0 || Array.IndexOf(args, "--tab") >= 0) MainPage.StartExpanded = true;

        App.InitializeLogging();

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }
}
