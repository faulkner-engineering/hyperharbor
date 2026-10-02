using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Owns the notification area icon, its menu, and the pairing and device windows.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _status;
    private readonly TrayPipeClient _pipe;
    private IReadOnlyList<TrayDevice> _devices = [];
    private PinForm? _pinForm;
    private DevicesForm? _devicesForm;

    public TrayApplicationContext()
    {
        _status = new ToolStripMenuItem("Connecting to host service…") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Paired devices…", null, (_, _) => ShowDevices());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HyperHarbor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDevices();

        _pipe = new TrayPipeClient();
        _pipe.ConnectionChanged += OnConnectionChanged;
        _pipe.MessageReceived += OnMessage;
        _pipe.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pipe.Dispose();
            _pinForm?.Dispose();
            _devicesForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnConnectionChanged(bool connected)
    {
        _status.Text = connected ? "Host service running" : "Host service not running";
        _notifyIcon.Text = connected ? "HyperHarbor" : "HyperHarbor (service not running)";
        if (!connected)
        {
            _pinForm?.Close();
        }
    }

    private void OnMessage(TrayMessage message)
    {
        switch (message)
        {
            case PairingStartedMessage started:
                ShowPin(started);
                break;
            case PairingEndedMessage ended:
                OnPairingEnded(ended);
                break;
            case DeviceListMessage list:
                _devices = list.Devices;
                _devicesForm?.ShowDevices(_devices);
                break;
            case WakeFixRequestedMessage wakeFix:
                _ = ApproveWakeFixAsync(wakeFix);
                break;
        }
    }

    private async Task ApproveWakeFixAsync(WakeFixRequestedMessage request)
    {
        var result = await WakeFixApproval.HandleAsync(request, _pipe.ServerExecutablePath());
        await _pipe.SendAsync(result);

        var (title, icon) = result.Outcome switch
        {
            "applied" => ("Wake-on-LAN settings updated", ToolTipIcon.Info),
            "failed" => ("Wake-on-LAN settings not updated", ToolTipIcon.Warning),
            _ => (string.Empty, ToolTipIcon.None),
        };
        if (title.Length > 0)
        {
            _notifyIcon.ShowBalloonTip(5000, title, result.Detail ?? "Check the settings again from the client.", icon);
        }
    }

    private void ShowPin(PairingStartedMessage started)
    {
        _pinForm?.Close();
        _pinForm = new PinForm(started.DeviceName, started.Pin, started.ExpiresAt, () => _ = _pipe.SendAsync(new CancelPairingMessage()))
        {
            PairingId = started.PairingId,
        };
        _pinForm.FormClosed += (sender, _) =>
        {
            if (ReferenceEquals(sender, _pinForm))
            {
                _pinForm = null;
            }
        };
        _pinForm.Show();
        _pinForm.Activate();

        _notifyIcon.ShowBalloonTip(5000, "Pairing request", $"\"{started.DeviceName}\" wants to pair with this PC.", ToolTipIcon.Info);
    }

    private void OnPairingEnded(PairingEndedMessage ended)
    {
        if (_pinForm?.PairingId == ended.PairingId)
        {
            _pinForm.Close();
        }

        var (title, text, icon) = ended.Outcome switch
        {
            "paired" => ("Device paired", $"\"{ended.DeviceName}\" can now connect to this PC.", ToolTipIcon.Info),
            "tooManyAttempts" => ("Pairing failed", $"Too many incorrect PINs from \"{ended.DeviceName}\".", ToolTipIcon.Warning),
            "expired" => ("Pairing expired", $"The PIN for \"{ended.DeviceName}\" expired.", ToolTipIcon.None),
            _ => (string.Empty, string.Empty, ToolTipIcon.None),
        };

        if (title.Length > 0)
        {
            _notifyIcon.ShowBalloonTip(5000, title, text, icon);
        }
    }

    private void ShowDevices()
    {
        if (_devicesForm is null)
        {
            _devicesForm = new DevicesForm(id => _pipe.SendAsync(new RemoveDeviceMessage(id)));
            _devicesForm.FormClosed += (_, _) => _devicesForm = null;
            _devicesForm.ShowDevices(_devices);
            _ = _pipe.SendAsync(new ListDevicesMessage());
        }

        _devicesForm.Show();
        _devicesForm.Activate();
    }
}
