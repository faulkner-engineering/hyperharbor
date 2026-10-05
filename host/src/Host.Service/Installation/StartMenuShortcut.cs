using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>"HyperHarbor Host" in the Start menu for all users: starts the tray, or opens its window if it runs.</summary>
internal static class StartMenuShortcut
{
    private static string Path =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "HyperHarbor Host.lnk");

    public static void Create(InstallLayout layout)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(Path);
            shortcut.TargetPath = layout.CurrentExecutable;
            shortcut.Arguments = HostCommandLine.TraySwitch;
            shortcut.WorkingDirectory = layout.Current;
            shortcut.Description = "Open the HyperHarbor Host window";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
