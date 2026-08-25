using System.Windows;

namespace Cube7ControlCenter;

public partial class App : Application
{
    private void App_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var window = new MainWindow(startBridge: false);
                window.Close();
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                Environment.ExitCode = 1;
            }
            Shutdown(Environment.ExitCode);
            return;
        }

        var main = new MainWindow(startBridge: true);
        MainWindow = main;
        main.Show();
    }
}
