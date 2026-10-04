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
    private string? _isoFolder;
    private bool _isoFolderChanging;
    private VmFolderMessage? _vmFolder;
    private bool _vmFolderChanging;
    private string? _backupFolder;
    private bool _backupFolderChanging;

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
            _isoFolder = null;
            _vmFolder = null;
            _backupFolder = null;
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
            case VmFolderMessage vmFolder:
                OnVmFolder(vmFolder);
                break;
            case IsoFolderMessage isoFolder:
                OnIsoFolder(isoFolder);
                break;
            case BackupFolderMessage backupFolder:
                OnBackupFolder(backupFolder);
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
            _hostForm = new HostForm(ShowPassphrase, ShowDevices, ChangeIsoFolder, ChangeVmFolder, ChangeBackupFolder, () => _ = SetUpConsoleAsync());
            _hostForm.FormClosed += (_, _) => _hostForm = null;
            RefreshHost();
        }

        _hostForm.Show();
        _hostForm.Activate();
    }

    private async Task SetUpConsoleAsync()
    {
        var answer = MessageBox.Show(
            _hostForm,
            "HyperHarbor will create a standard local account on this PC for each HyperHarbor user (for example hhc-owner). " +
            "It cannot sign in to Windows; paired devices use it only to open the consoles of VMs, and its password changes every time they do." +
            $"{Environment.NewLine}{Environment.NewLine}Windows will ask for administrator permission. Continue?",
            "Set up console access",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        // The service executable sits beside the tray in the packaged host, so setup also works while the service is stopped.
        var executable = _pipe.ServerExecutablePath() ?? Path.Combine(AppContext.BaseDirectory, "HyperHarbor.Host.Service.exe");
        var (succeeded, message) = await ConsoleAccessSetup.RunAsync(executable, HostForm.DataDirectory);
        RefreshHost();
        MessageBox.Show(_hostForm, message, "Set up console access", MessageBoxButtons.OK, succeeded ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ChangeIsoFolder()
    {
        if (ChooseFolder("Choose where the host stores the ISO images clients add. Images already in the current folder stay there.", _isoFolder) is { } folder)
        {
            _isoFolderChanging = true;
            _ = _pipe.SendAsync(new SetIsoFolderMessage(folder));
        }
    }

    private void ChangeVmFolder()
    {
        var current = _vmFolder is { IsDefault: false } chosen ? chosen.Folder : null;
        if (ChooseFolder("Choose where the host creates new VMs. Each VM gets its own folder here; existing VMs stay where they are.", current) is { } folder)
        {
            _vmFolderChanging = true;
            _ = _pipe.SendAsync(new SetVmFolderMessage(folder));
        }
    }

    private void ChangeBackupFolder()
    {
        if (ChooseFolder("Choose where the host puts VM disk exports. Each export gets its own folder here; earlier exports stay where they are.", _backupFolder) is { } folder)
        {
            _backupFolderChanging = true;
            _ = _pipe.SendAsync(new SetBackupFolderMessage(folder));
        }
    }

    /// <returns>The chosen folder, or null when the user cancelled or kept the current one.</returns>
    private string? ChooseFolder(string description, string? current)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = current ?? string.Empty,
            ShowNewFolderButton = true,
        };
        return dialog.ShowDialog(_hostForm) == DialogResult.OK && !string.Equals(dialog.SelectedPath, current, StringComparison.OrdinalIgnoreCase)
            ? dialog.SelectedPath
            : null;
    }

    private void OnIsoFolder(IsoFolderMessage message)
    {
        _isoFolder = message.Folder;
        RefreshHost();
        if (_isoFolderChanging)
        {
            _isoFolderChanging = false;
            ReportFolderChange(message.Error, "ISO library moved", $"New images are stored in {message.Folder}.");
        }
    }

    private void OnVmFolder(VmFolderMessage message)
    {
        _vmFolder = message;
        RefreshHost();
        if (_vmFolderChanging)
        {
            _vmFolderChanging = false;
            ReportFolderChange(message.Error, "VM storage changed", $"New VMs are created in {message.Folder}.");
        }
    }

    private void OnBackupFolder(BackupFolderMessage message)
    {
        _backupFolder = message.Folder;
        RefreshHost();
        if (_backupFolderChanging)
        {
            _backupFolderChanging = false;
            ReportFolderChange(message.Error, "Backup folder changed", $"Disk exports go to {message.Folder}.");
        }
    }

    private void ReportFolderChange(string? error, string title, string text)
    {
        if (error is not null)
        {
            MessageBox.Show(_hostForm, error, "HyperHarbor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
    }

    private void RefreshHost() =>
        _hostForm?.ShowStatus(new HostStatus(_connected, _passphraseConfigured, _devices.Count, _isoFolder, _vmFolder, _backupFolder));

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
