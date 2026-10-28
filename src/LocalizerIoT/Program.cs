using System.Diagnostics;
using System.Security.Principal;

namespace LocalizerIoT;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show("This application requires Windows 10/11.", "Localizer-IoT");
            return;
        }
        Application.Run(new MainForm());
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
