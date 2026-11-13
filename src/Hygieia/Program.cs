using System.Runtime.InteropServices;

namespace Hygieia;

static class Program
{
    [DllImport("shell32", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    static void Main()
    {
        try { SetCurrentProcessExplicitAppUserModelID("Hygieia.SecureErase"); }
        catch (Exception) { }
        Theme.PreferDark();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
