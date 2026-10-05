using System.Diagnostics;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// The tray's main window: service status, the admin passphrase, paired devices, console access,
/// storage folders, and the host's logs.
/// Opened by double-clicking the tray icon. The layout sizes itself from its contents so it stays
/// readable at any display scaling.
/// </summary>
internal sealed class HostForm : Form
{
    /// <summary>Where the service keeps its data unless DataDirectory is configured.</summary>
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperHarbor");

    private readonly Label _service;
    private readonly Label _passphrase;
    private readonly Button _setPassphrase;
    private readonly Label _devices;
    private readonly Button _manageDevices;
    private readonly Label _isoFolder;
    private readonly Button _changeIsoFolder;
    private readonly Label _vmFolder;
    private readonly Button _changeVmFolder;
    private readonly Label _backupFolder;
    private readonly Button _changeBackupFolder;
    private readonly Label _console;
    private readonly Button _setUpConsole;
    private readonly Label _update;
    private readonly Button _updateAction;
    private readonly Label _updateChannel;
    private readonly Button _changeUpdateChannel;
    private readonly ContextMenuStrip _channels = new();
    private readonly Action<string> _setUpdateChannel;
    private HostUpdateStatus? _updateStatus;

    /// <param name="checkOrInstallUpdate">Called with true to install the ready version, false to check now.</param>
    public HostForm(
        Action setPassphrase,
        Action manageDevices,
        Action changeIsoFolder,
        Action changeVmFolder,
        Action changeBackupFolder,
        Action setUpConsole,
        Action<bool> checkOrInstallUpdate,
        Action<string> setUpdateChannel)
    {
        _setUpdateChannel = setUpdateChannel;
        // Design at 96 DPI; WinForms scales fonts and padding to the monitor's DPI.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Text = "HyperHarbor Host";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(20);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _service = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 14) };
        layout.Controls.Add(_service);
        layout.SetColumnSpan(_service, 2);

        _passphrase = AddRow(layout, "Admin passphrase", out _setPassphrase, "Set admin passphrase…", setPassphrase);
        _devices = AddRow(layout, "Paired devices", out _manageDevices, "Manage devices…", manageDevices);
        _console = AddRow(layout, "VM console", out _setUpConsole, "Set up console access…", setUpConsole);
        _vmFolder = AddRow(layout, "VM storage", out _changeVmFolder, "Change folder…", changeVmFolder);
        _isoFolder = AddRow(layout, "ISO library", out _changeIsoFolder, "Change folder…", changeIsoFolder);
        _backupFolder = AddRow(layout, "VM backups", out _changeBackupFolder, "Change folder…", changeBackupFolder);
        _update = AddRow(layout, "Updates", out _updateAction, "Check now", () => checkOrInstallUpdate(_updateStatus?.Activity == HostUpdateActivity.Ready));
        _updateChannel = AddRow(layout, "Update channel", out _changeUpdateChannel, "Change channel…", ShowChannels);
        AddRow(layout, "Host log", out _, "Open logs folder", () => Open(Path.Combine(DataDirectory, "logs"), folder: true), $"Daily files in {Path.Combine(DataDirectory, "logs")}");
        AddRow(layout, "Audit log", out _, "Open audit log", () => Open(Path.Combine(DataDirectory, "audit.log"), folder: false), "Every change made from a client or this tray");

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(12, 2, 12, 2), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        layout.Controls.Add(new Label { AutoSize = true });
        layout.Controls.Add(close);
        CancelButton = close;

        Controls.Add(layout);
        ShowStatus(new HostStatus(Connected: false, PassphraseConfigured: null, DeviceCount: 0, IsoFolder: null, VmFolder: null, BackupFolder: null));
        ShowUpdate(null);
    }

    private void ShowChannels() => _channels.Show(_changeUpdateChannel, new Point(0, _changeUpdateChannel.Height));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _channels.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Shows the update status; null while the service is not connected.</summary>
    public void ShowUpdate(HostUpdateStatus? status)
    {
        _updateStatus = status;
        _updateAction.Text = status?.Activity == HostUpdateActivity.Ready ? "Install now" : "Check now";
        _updateAction.Enabled = status is { Supported: true } && status.Activity is HostUpdateActivity.Idle or HostUpdateActivity.Ready;
        _changeUpdateChannel.Enabled = status is { Supported: true };
        _update.ForeColor = SystemColors.ControlText;

        if (status is null)
        {
            _update.Text = "Unknown while the service is not running.";
            _updateChannel.Text = string.Empty;
            return;
        }

        if (!status.Supported)
        {
            _update.Text = $"Version {status.CurrentVersion}. {status.Message}";
            _updateChannel.Text = "Install the host to follow a release channel.";
            return;
        }

        var lines = new List<string> { $"Version {status.CurrentVersion}." };
        lines.Add(status.Activity switch
        {
            HostUpdateActivity.Checking => "Checking for updates…",
            HostUpdateActivity.Preparing => $"Downloading and testing version {status.AvailableVersion}…",
            HostUpdateActivity.Ready => $"Version {status.AvailableVersion} is ready. " + (status.Mode == HostUpdateMode.Auto
                ? "It installs when nothing is in progress, or at the maintenance time."
                : "Choose Install now to install it; the host restarts."),
            HostUpdateActivity.Installing => $"Installing version {status.AvailableVersion}. The host restarts.",
            _ => status.Message ?? (status.LastCheck is null ? "Not checked yet." : "Up to date."),
        });
        if (status.Activity == HostUpdateActivity.Idle && status.Message is not null && status.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            _update.ForeColor = Color.Firebrick;
        }

        if (status.LastResult is { } result)
        {
            lines.Add($"Last update: {result}");
        }

        if (status.LastCheck is { } checkedAt)
        {
            lines.Add($"Checked {checkedAt.ToLocalTime():g}.");
        }

        _update.Text = string.Join(" ", lines);

        var mode = status.Mode switch
        {
            HostUpdateMode.Notify => "Updates download automatically; you choose when to install.",
            HostUpdateMode.Off => "Automatic checks are off.",
            _ => status.MaintenanceTime is { } time
                ? $"Updates install automatically when nothing is in progress, or after {time}."
                : "Updates install automatically when nothing is in progress.",
        };
        _updateChannel.Text = $"Following the {status.Channel} channel. {mode}";

        _channels.Items.Clear();
        foreach (var channel in status.Channels)
        {
            var item = new ToolStripMenuItem(channel) { Checked = string.Equals(channel, status.Channel, StringComparison.OrdinalIgnoreCase) };
            item.Click += (_, _) => _setUpdateChannel(channel);
            _channels.Items.Add(item);
        }
    }

    public void ShowStatus(HostStatus status)
    {
        var (connected, passphraseConfigured, deviceCount, isoFolder, vmFolder, backupFolder) = status;
        _service.Text = connected
            ? "Host service running"
            : "Host service not running. Start the HyperHarbor Host service, or Start-HyperHarbor.ps1 for a host that is not installed.";
        _service.ForeColor = connected ? SystemColors.ControlText : Color.Firebrick;

        _vmFolder.Text = connected && vmFolder is not null
            ? $"New VMs are created in {vmFolder.Folder}{(vmFolder.IsDefault ? " (the Hyper-V default)" : string.Empty)}. Existing VMs stay where they are."
            : "Unknown while the service is not running.";
        _changeVmFolder.Enabled = connected && vmFolder is not null;

        _passphrase.Text = passphraseConfigured switch
        {
            true => "Set. Paired devices enter it to delete, create, or change VMs.",
            false => "Not set. Paired devices cannot delete, create, or change VMs until you set one.",
            null => "Unknown while the service is not running.",
        };
        _passphrase.ForeColor = passphraseConfigured == false ? Color.Firebrick : SystemColors.ControlText;
        _setPassphrase.Text = passphraseConfigured == true ? "Change admin passphrase…" : "Set admin passphrase…";
        _setPassphrase.Enabled = connected;

        _devices.Text = !connected ? "Unknown while the service is not running."
            : deviceCount == 1 ? "1 device is paired." : $"{deviceCount} devices are paired.";
        _manageDevices.Enabled = connected;

        _isoFolder.Text = connected && isoFolder is not null
            ? $"Images clients add are stored in {isoFolder}."
            : "Unknown while the service is not running.";
        _changeIsoFolder.Enabled = connected && isoFolder is not null;

        _backupFolder.Text = connected && backupFolder is not null
            ? $"Disk exports a client starts without choosing a folder go to {backupFolder}."
            : "Unknown while the service is not running.";
        _changeBackupFolder.Enabled = connected && backupFolder is not null;

        // The setup helper writes this file itself, so the status does not depend on the service.
        var consoleReady = ConsoleAccessSetup.IsSetUp(DataDirectory);
        _console.Text = consoleReady
            ? "Set up. Paired devices can open the console of any running VM."
            : "Not set up. Paired devices cannot open VM consoles until you set it up. Windows asks for administrator permission.";
        _console.ForeColor = consoleReady ? SystemColors.ControlText : Color.Firebrick;
        _setUpConsole.Text = consoleReady ? "Set up again…" : "Set up console access…";
    }

    private static Label AddRow(TableLayoutPanel layout, string heading, out Button button, string buttonText, Action onClick, string? description = null)
    {
        var text = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        if (description is not null)
        {
            text.Text = description;
        }

        var block = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, 0, 16, 12),
            WrapContents = false,
        };
        block.Controls.Add(new Label { Text = heading, AutoSize = true, Font = new Font(layout.Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
        block.Controls.Add(text);

        button = new Button { Text = buttonText, AutoSize = true, Padding = new Padding(10, 2, 10, 2), Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 0, 12) };
        button.Click += (_, _) => onClick();

        layout.Controls.Add(block);
        layout.Controls.Add(button);
        return text;
    }

    private static void Open(string path, bool folder)
    {
        if (folder ? !Directory.Exists(path) : !File.Exists(path))
        {
            MessageBox.Show($"{path} does not exist yet. The host service creates it when it starts.", "HyperHarbor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // The log files grant access only to Administrators, SYSTEM, and the account running the service.
            Process.Start(new ProcessStartInfo(folder ? "explorer.exe" : "notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show($"Could not open {path}: {ex.Message}", "HyperHarbor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>What the host window shows. Values the service has not reported yet are null.</summary>
internal sealed record HostStatus(
    bool Connected,
    bool? PassphraseConfigured,
    int DeviceCount,
    string? IsoFolder,
    Shared.Contracts.Ipc.VmFolderMessage? VmFolder,
    string? BackupFolder);
