using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.GameReady;

/// <summary>Lists the present Plug and Play devices; <see cref="ControllerClassifier"/> picks the game controllers from them.</summary>
public interface IPnpDeviceReader
{
    Task<IReadOnlyList<PnpNode>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>The powercfg and power policy operations the Game ready cards need.</summary>
public interface IPowerConfig
{
    /// <summary>The output of "powercfg /lastwake".</summary>
    Task<string> LastWakeAsync(CancellationToken cancellationToken);

    /// <summary>The USB selective suspend setting of the active plan on AC power; null when it cannot be read.</summary>
    bool? UsbSelectiveSuspendOnAc();

    /// <summary>"powercfg /deviceenablewake". Needs administrator rights.</summary>
    /// <exception cref="InvalidOperationException">powercfg refused.</exception>
    void EnableWake(string powercfgName);

    /// <summary>Turns USB selective suspend off on AC power in the active plan. Needs administrator rights.</summary>
    /// <exception cref="InvalidOperationException">powercfg refused.</exception>
    void DisableUsbSelectiveSuspendOnAc();
}

public sealed class CimPnpDeviceReader : IPnpDeviceReader
{
    public Task<IReadOnlyList<PnpNode>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<PnpNode>>(Read, cancellationToken);

    private static List<PnpNode> Read()
    {
        using var session = CimSession.Create(null);
        var nodes = new List<PnpNode>();
        foreach (var instance in session.QueryInstances(@"root\cimv2", "WQL", "SELECT Name, PNPDeviceID, PNPClass, Manufacturer FROM Win32_PnPEntity"))
        {
            using (instance)
            {
                string? Text(string name) => instance.CimInstanceProperties[name]?.Value as string;
                if (Text("Name") is { } name && Text("PNPDeviceID") is { } id)
                {
                    nodes.Add(new PnpNode(name, id, Text("PNPClass"), Text("Manufacturer")));
                }
            }
        }

        return nodes;
    }
}

public sealed class WindowsPowerConfig : IPowerConfig
{
    private static readonly Guid UsbSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    public async Task<string> LastWakeAsync(CancellationToken cancellationToken) =>
        await RunAsync(["/lastwake"], cancellationToken);

    public bool? UsbSelectiveSuspendOnAc()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) != 0)
        {
            return null;
        }

        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            var subgroup = UsbSubgroup;
            var setting = UsbSelectiveSuspend;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0 ? value != 0 : null;
        }
        finally
        {
            LocalFree(schemePointer);
        }
    }

    public void EnableWake(string powercfgName) => RunChecked("/deviceenablewake", powercfgName);

    public void DisableUsbSelectiveSuspendOnAc()
    {
        RunChecked("/setacvalueindex", "SCHEME_CURRENT", UsbSubgroup.ToString("D"), UsbSelectiveSuspend.ToString("D"), "0");
        RunChecked("/setactive", "SCHEME_CURRENT");
    }

    internal static void RunChecked(params string[] arguments)
    {
        using var process = Start(arguments);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"powercfg exited with code {process.ExitCode}: {output.Trim()}");
        }
    }

    private static async Task<string> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using var process = Start(arguments);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return output;
    }

    /// <summary>Arguments are passed one by one, so a device name is never parsed as a command line.</summary>
    private static Process Start(string[] arguments)
    {
        var start = new ProcessStartInfo("powercfg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)!;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
