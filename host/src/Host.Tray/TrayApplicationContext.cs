using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Owns the notification area icon, its menu, and the host, pairing, device, and passphrase windows.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _status;
    private readonly TrayPipeClient _pipe;
    private IReadOnlyList<TrayDevice> _devices = [];
    private PinForm? _pinForm;
    private DevicesForm? _devicesForm;
    private readonly ToolStripMenuItem _passphraseItem;
    private AdminPassphraseForm? _passphraseForm;
    private HostForm? _hostForm;
    private bool _connected;

    /// <summary>Null until the service reports it.</summary>
    private bool? _passphraseConfigured;
    private bool _passphraseSaving;

    public TrayApplicationContext()
    {
        _status = new ToolStripMenuItem("Connecting to host service…") { Enabled = false };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open HyperHarbor Host…", null, (_, _) => ShowHost()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Paired devices…", null, (_, _) => ShowDevices());
        _passphraseItem = new ToolStripMenuItem("Set admin passphrase…", null, (_, _) => ShowPassphrase()) { Enabled = false };
        menu.Items.Add(_passphraseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "HyperHarbor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHost();

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
            _hostForm?.Dispose();
            _passphraseForm?.Dispose();
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
        _passphraseItem.Enabled = connected;
        _connected = connected;
        if (!connected)
        {
            _passphraseConfigured = null;
        }

        RefreshHost();
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
                RefreshHost();
                break;
            case AdminPassphraseStatusMessage status:
                OnPassphraseStatus(status);
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

    private void ShowPassphrase()
    {
        if (_passphraseForm is null)
        {
            _passphraseForm = new AdminPassphraseForm(_passphraseConfigured == true, async hash =>
            {
                _passphraseSaving = true;
                await _pipe.SendAsync(hash);
            });
            _passphraseForm.FormClosed += (_, _) => _passphraseForm = null;
        }

        _passphraseForm.Show();
        _passphraseForm.Activate();
    }

    private void ShowHost()
    {
        if (_hostForm is null)
        {
            _hostForm = new HostForm(ShowPassphrase, ShowDevices);
            _hostForm.FormClosed += (_, _) => _hostForm = null;
            RefreshHost();
        }

        _hostForm.Show();
        _hostForm.Activate();
    }

    private void RefreshHost() => _hostForm?.ShowStatus(_connected, _passphraseConfigured, _devices.Count);

    private void OnPassphraseStatus(AdminPassphraseStatusMessage status)
    {
        _passphraseConfigured = status.Configured;
        _passphraseItem.Text = status.Configured ? "Change admin passphrase…" : "Set admin passphrase…";
        RefreshHost();
        if (_passphraseSaving && status.Configured)
        {
            _passphraseSaving = false;
            _notifyIcon.ShowBalloonTip(5000, "Admin passphrase saved", "Paired devices now need it to change VMs.", ToolTipIcon.Info);
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
