using System.Runtime.InteropServices;
using HyperHarbor.Host.Tray.Ambient;

namespace HyperHarbor.Host.Tray;

/// <summary>A number that changes whenever a game controller's state changes; null when no controller is connected.</summary>
internal interface IControllerInput
{
    long? Poll();
}

/// <summary>Turns the display on.</summary>
internal interface IDisplayControl
{
    void ForceOn();
}

/// <summary>
/// Console idle turns the display off to save power and expects a controller press to bring the TV back. Windows counts
/// keyboard and mouse input (and what Steam Input injects) as activity, but XInput activity does not always reset the
/// display idle timer. While the display is off, this watches the controllers and, on a change, asks Windows for the
/// display once. It does nothing while the display is on, so a game that reads XInput is never touched.
/// </summary>
internal sealed class DisplayWake(IControllerInput input, IDisplayControl display, TimeProvider time)
{
    /// <summary>A second change within this time does not ask again.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(3);

    private long? _last;
    private DateTimeOffset? _lastForced;

    /// <summary>True in Console idle mode.</summary>
    public bool Enabled { get; set; }

    /// <summary>True while the display is off or dimmed.</summary>
    public bool DisplayOff { get; set; }

    /// <summary>How many times the display was asked to turn on.</summary>
    public int Forced { get; private set; }

    /// <summary>Called a few times a second.</summary>
    public void Tick()
    {
        if (!Enabled || !DisplayOff)
        {
            // The next time the display goes off, the state at that moment is the starting point.
            _last = null;
            return;
        }

        var state = input.Poll();
        if (state is null)
        {
            _last = null;
            return;
        }

        var changed = _last is { } previous && previous != state;
        _last = state;
        if (!changed)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (_lastForced is { } at && now - at < MinimumGap)
        {
            return;
        }

        _lastForced = now;
        Forced++;
        display.ForceOn();
    }
}

/// <summary>XInput through xinput1_4.dll (Windows 8 and later), polled for the four player slots.</summary>
internal sealed class XInputControllers : IControllerInput, IControllerButtons
{
    private const uint Success = 0;

    // A resting stick or trigger wobbles, so only a clear movement counts as use of the controller.
    private const int StickDeadZone = 8_000;
    private const int TriggerDeadZone = 30;

    /// <summary>The ambient screen's reading: buttons and clear stick or trigger movement, not the packet counter, which also counts noise.</summary>
    public ControllerSample? Sample()
    {
        long? token = null;
        var pads = new List<ushort>();
        for (uint slot = 0; slot < 4; slot++)
        {
            try
            {
                if (XInputGetState(slot, out var pad) == Success)
                {
                    token = HashCode.Combine(
                        token ?? 17,
                        slot,
                        pad.Buttons,
                        Zoned(pad.ThumbLeftX),
                        Zoned(pad.ThumbLeftY),
                        Zoned(pad.ThumbRightX),
                        Zoned(pad.ThumbRightY));
                    token = HashCode.Combine(token, pad.LeftTrigger > TriggerDeadZone, pad.RightTrigger > TriggerDeadZone);
                    pads.Add(pad.Buttons);
                }
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        return token is { } value ? new ControllerSample(value, pads) : null;
    }

    private static int Zoned(short axis) => Math.Abs((int)axis) < StickDeadZone ? 0 : Math.Sign(axis);

    public long? Poll()
    {
        long? state = null;
        for (uint slot = 0; slot < 4; slot++)
        {
            try
            {
                if (XInputGetState(slot, out var pad) == Success)
                {
                    state = HashCode.Combine(state ?? 17, slot, pad.PacketNumber);
                }
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        return state;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLeftX;
        public short ThumbLeftY;
        public short ThumbRightX;
        public short ThumbRightY;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}

/// <summary>SetThreadExecutionState(ES_DISPLAY_REQUIRED) without ES_CONTINUOUS: one call that resets the display idle timer.</summary>
internal sealed class ExecutionStateDisplay : IDisplayControl
{
    private const uint EsDisplayRequired = 0x00000002;

    public void ForceOn() => _ = SetThreadExecutionState(EsDisplayRequired);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}

/// <summary>
/// Hears when the display turns off or on (GUID_CONSOLE_DISPLAY_STATE: 0 off, 1 on, 2 dimmed) through a hidden
/// message-only window.
/// </summary>
internal sealed class DisplayStateWatcher : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int HwndMessage = -3;
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private readonly Action<bool> _displayOff;
    private IntPtr _registration;

    public DisplayStateWatcher(Action<bool> displayOff)
    {
        _displayOff = displayOff;
        CreateHandle(new CreateParams { Parent = new IntPtr(HwndMessage) });
        var setting = ConsoleDisplayState;
        _registration = RegisterPowerSettingNotification(Handle, ref setting, 0);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_registration);
            _registration = IntPtr.Zero;
        }

        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmPowerBroadcast && m.WParam.ToInt32() == PbtPowerSettingChange && m.LParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(m.LParam);
            if (setting.PowerSetting == ConsoleDisplayState && setting.DataLength >= 4)
            {
                var state = Marshal.ReadInt32(m.LParam, Marshal.SizeOf<PowerBroadcastSetting>());
                _displayOff(state != 1);
            }
        }

        base.WndProc(ref m);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}

/// <summary>
/// Reports whether a game runs in the foreground on this desktop, so the scheduled sleep does not interrupt it.
/// SHQueryUserNotificationState names a full-screen Direct3D application (3) or presentation mode (4). Borderless
/// windowed games and Steam Big Picture are not recognized, on purpose: Big Picture is always full screen.
/// </summary>
internal sealed class ForegroundGameReporter(Func<int?> queryState, Action<bool> send)
{
    private const int RunningD3dFullScreen = 3;
    private const int PresentationMode = 4;

    /// <summary>The service ignores a report older than two minutes, so an unchanged answer is sent again this often (in ticks).</summary>
    public const int RepeatEvery = 4;

    private bool? _last;
    private int _sinceSent;

    public void Tick()
    {
        var active = queryState() is RunningD3dFullScreen or PresentationMode;
        if (_last == active && ++_sinceSent < RepeatEvery)
        {
            return;
        }

        _last = active;
        _sinceSent = 0;
        send(active);
    }

    public static int? QueryWindowsState() => SHQueryUserNotificationState(out var state) == 0 ? state : null;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
