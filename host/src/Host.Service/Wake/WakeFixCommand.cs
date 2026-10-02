using HyperHarbor.Host.Core.Wake;

namespace HyperHarbor.Host.Service.Wake;

/// <summary>
/// The elevated helper: HyperHarbor.Host.Service.exe --apply-wake-fixes id1,id2. The tray starts it
/// with a UAC prompt after the user approves. Exit code 0 means every fix applied.
/// </summary>
internal static class WakeFixCommand
{
    public const int ExitApplied = 0;
    public const int ExitPartial = 1;
    public const int ExitNotElevated = 2;
    public const int ExitInvalid = 3;

    public static async Task<int> RunAsync(string checkIdList)
    {
        var checkIds = checkIdList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (checkIds.Length == 0 || checkIds.Any(id => !WakeCheckIds.Fixable.Contains(id)))
        {
            Console.Error.WriteLine($"Unknown or unfixable check IDs: {checkIdList}");
            return ExitInvalid;
        }

        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Applying Wake-on-LAN fixes requires administrator rights.");
            return ExitNotElevated;
        }

        var environment = await new WindowsWakeEnvironmentReader().ReadAsync(CancellationToken.None);
        var results = WakeFixer.Apply(environment, checkIds);
        foreach (var result in results)
        {
            Console.WriteLine($"{result.CheckId}: {(result.Applied ? "applied" : "failed")} - {result.Detail}");
        }

        return results.All(result => result.Applied) ? ExitApplied : ExitPartial;
    }
}
