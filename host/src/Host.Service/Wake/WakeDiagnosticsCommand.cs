namespace HyperHarbor.Host.Service.Wake;

/// <summary>
/// HyperHarbor.Host.exe save-wake-diagnostics [folder]: writes Diagnose-Wake.ps1, carried inside the executable,
/// which collects what the Wake-on-LAN readiness checks cannot see.
/// </summary>
internal static class WakeDiagnosticsCommand
{
    private const string ResourceName = "HyperHarbor.Host.Diagnose-Wake.ps1";
    private const string FileName = "Diagnose-Wake.ps1";

    public static int Run(string[] args)
    {
        var folder = Path.GetFullPath(args.Length > 0 ? args[0] : Environment.CurrentDirectory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);

        using (var resource = typeof(WakeDiagnosticsCommand).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is missing from the executable."))
        using (var file = File.Create(path))
        {
            resource.CopyTo(file);
        }

        Console.WriteLine($"Wrote {path}");
        Console.WriteLine($"Run it in PowerShell: powershell -ExecutionPolicy Bypass -File \"{path}\" > wake-report.txt");
        return 0;
    }
}
