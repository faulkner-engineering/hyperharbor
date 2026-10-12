using System.Runtime.InteropServices;
using System.Text.Json;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.GameReady.PowerMode;

/// <summary>The record as a JSON file in the data folder. It holds timeouts and times, no secret.</summary>
public sealed class PowerModeStore(string dataDirectory) : IPowerModeStore
{
    public const string FileName = "power-mode.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _path = Path.Combine(dataDirectory, FileName);

    public PowerModeRecord? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PowerModeRecord>(Utf8Json.ReadFile(_path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(PowerModeRecord record)
    {
        Directory.CreateDirectory(dataDirectory);
        var partial = _path + ".partial";
        File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(record, Options));
        File.Move(partial, _path, overwrite: true);
    }
}

/// <summary>
/// Reads and writes the active plan through powrprof and powercfg. Only the active scheme's AC values are used, by GUID
/// (the aliases are not the same on every Windows edition), and a plan is never duplicated.
/// </summary>
public sealed class WindowsPowerPlans : IPowerPlans
{
    private static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid HibernateIdle = new("9d7815a6-7ee4-497e-8888-515a05f02364");
    private static readonly Guid WakeTimers = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");
    private static readonly Guid UnattendedSleep = new("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    public Guid ActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0)
        {
            throw new InvalidOperationException("Windows did not report the active power plan.");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public PowerValues? Read(Guid plan)
    {
        var sleep = ReadValue(plan, SleepSubgroup, StandbyIdle);
        var hibernate = ReadValue(plan, SleepSubgroup, HibernateIdle);
        var display = ReadValue(plan, VideoSubgroup, VideoIdle);
        var wake = ReadValue(plan, SleepSubgroup, WakeTimers);
        var unattended = ReadValue(plan, SleepSubgroup, UnattendedSleep);
        return sleep is null || hibernate is null || display is null || wake is null || unattended is null
            ? null
            : new PowerValues(sleep.Value, hibernate.Value, display.Value, wake.Value, unattended.Value);
    }

    public void Write(Guid plan, PowerValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        WriteValue(plan, SleepSubgroup, StandbyIdle, values.SleepSeconds);
        WriteValue(plan, SleepSubgroup, HibernateIdle, values.HibernateSeconds);
        WriteValue(plan, VideoSubgroup, VideoIdle, values.DisplaySeconds);
        WriteValue(plan, SleepSubgroup, WakeTimers, values.WakeTimers);
        WriteValue(plan, SleepSubgroup, UnattendedSleep, values.UnattendedSleepSeconds);

        // Applies the values when the plan is the active one.
        WindowsPowerConfig.RunChecked("/setactive", plan.ToString("D"));
    }

    private static int? ReadValue(Guid plan, Guid subgroup, Guid setting) =>
        PowerReadACValueIndex(IntPtr.Zero, ref plan, ref subgroup, ref setting, out var value) == 0 ? (int)value : null;

    private static void WriteValue(Guid plan, Guid subgroup, Guid setting, int value) =>
        WindowsPowerConfig.RunChecked("/setacvalueindex", plan.ToString("D"), subgroup.ToString("D"), setting.ToString("D"), value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
