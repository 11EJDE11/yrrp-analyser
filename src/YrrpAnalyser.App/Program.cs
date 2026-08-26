using System.Windows.Forms;

namespace YrrpAnalyser.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.AddMessageFilter(new WheelRouter());

        var form = new MainForm();
        if (args.Length > 0 && File.Exists(args[0]))
            form.Shown += (_, _) => form.OpenOnStartup(args[0]);

        Application.Run(form);
    }
}
