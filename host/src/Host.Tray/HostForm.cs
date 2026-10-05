using System.Diagnostics;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// The tray's main window: service status, the admin passphrase, paired devices, console access,
/// storage folders, and the host's logs.
/// Opened by double-clicking the tray icon. The sections are on tabs whose pages scroll, and the window is
/// sized from its contents but never beyond the screen's work area, so it stays usable at any display scaling.
/// </summary>
internal sealed class HostForm : Form
{
    /// <summary>Where the service keeps its data unless DataDirectory is configured.</summary>
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperHarbor");

    private readonly Label _service;
    private readonly TabControl _tabs;
    private readonly TabPage _updatesPage;
    private readonly List<TabPage> _pages = [];
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

        Text = "HyperHarbor Host";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        // OnLoad sizes the window and centers it on the screen with the mouse (the tray icon's screen).
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(16);
        // A starting size so the first layout is meaningful; OnLoad replaces it.
        ClientSize = new Size(800, 600);

        var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill };
        // The one column fills the window, so the tabs take the window's width rather than their contents'.
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _service = new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10) };
        root.Controls.Add(_service, 0, 0);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        root.Controls.Add(_tabs, 0, 1);

        var overview = AddPage("Overview");
        _passphrase = AddRow(overview, "Admin passphrase", out _setPassphrase, "Set admin passphrase…", setPassphrase);
        _devices = AddRow(overview, "Paired devices", out _manageDevices, "Manage devices…", manageDevices);
        _console = AddRow(overview, "VM console", out _setUpConsole, "Set up console access…", setUpConsole);

        var storage = AddPage("Storage");
        _vmFolder = AddRow(storage, "VM storage", out _changeVmFolder, "Change folder…", changeVmFolder);
        _isoFolder = AddRow(storage, "ISO library", out _changeIsoFolder, "Change folder…", changeIsoFolder);
        _backupFolder = AddRow(storage, "VM backups", out _changeBackupFolder, "Change folder…", changeBackupFolder);

        var updates = AddPage("Updates");
        _updatesPage = _pages[^1];
        _update = AddRow(updates, "Updates", out _updateAction, "Check now", () => checkOrInstallUpdate(_updateStatus?.Activity == HostUpdateActivity.Ready));
        _updateChannel = AddRow(updates, "Update channel", out _changeUpdateChannel, "Change channel…", ShowChannels);

        var logs = AddPage("Logs");
        AddRow(logs, "Host log", out _, "Open logs folder", () => Open(Path.Combine(DataDirectory, "logs"), folder: true), $"Daily files in {Path.Combine(DataDirectory, "logs")}");
        AddRow(logs, "Audit log", out _, "Open audit log", () => Open(Path.Combine(DataDirectory, "audit.log"), folder: false), "Every change made from a client or this tray");

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(12, 2, 12, 2), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        root.Controls.Add(close, 0, 2);
        CancelButton = close;

        Controls.Add(root);
        ShowStatus(new HostStatus(Connected: false, PassphraseConfigured: null, DeviceCount: 0, IsoFolder: null, VmFolder: null, BackupFolder: null));
        ShowUpdate(null);
    }

    /// <summary>Opens centered on the screen with the mouse (the tray icon's screen), sized to the largest tab.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        FitToContent(workingArea);
        Bounds = WindowFit.Center(Size, workingArea);
    }

    /// <summary>
    /// Moving to a monitor with other scaling rescales every control, so the window is fitted again around its
    /// center and kept on that monitor's work area.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        var center = new Point(Left + (Width / 2), Top + (Height / 2));
        var workingArea = Screen.FromPoint(center).WorkingArea;
        FitToContent(workingArea);
        Bounds = WindowFit.PlaceInside(new Rectangle(center.X - (Width / 2), center.Y - (Height / 2), Width, Height), workingArea);
    }

    /// <summary>Sizes the window to its largest tab, within <paramref name="workingArea"/>; pages that still do not fit scroll.</summary>
    private void FitToContent(Rectangle workingArea)
    {
        var content = Size.Empty;
        foreach (var tab in _pages)
        {
            var preferred = tab.Controls[0].GetPreferredSize(new Size(int.MaxValue, int.MaxValue)) + tab.Padding.Size;
            content = new Size(Math.Max(content.Width, preferred.Width), Math.Max(content.Height, preferred.Height));
        }

        // Room for a vertical scroll bar, so a page that scrolls does not also need a horizontal one.
        content += new Size(SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) + LogicalToDeviceUnits(4), LogicalToDeviceUnits(4));
        MinimumSize = WindowFit.Fit(SizeFromClientSize(LogicalToDeviceUnits(new Size(440, 320))), workingArea, Size.Empty);

        // Everything around a tab page's client area: padding, the status line, the tab strip, and Close.
        // The status line wraps to the window's width, so the second pass measures it at the final width.
        for (var pass = 0; pass < 2; pass++)
        {
            PerformLayout();
            var page = _tabs.SelectedTab ?? _pages[0];
            var chrome = ClientSize - page.ClientSize;
            Size = WindowFit.Fit(SizeFromClientSize(content + chrome), workingArea, MinimumSize);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Wrap the status line (the "not running" text is long) instead of widening the window.
        if (_service is not null)
        {
            _service.MaximumSize = new Size(Math.Max(0, ClientSize.Width - Padding.Horizontal), 0);
        }
    }

    private TableLayoutPanel AddPage(string title)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(12), UseVisualStyleBackColor = true };
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Location = new Point(page.Padding.Left, page.Padding.Top),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        page.Controls.Add(table);
        _tabs.TabPages.Add(page);
        _pages.Add(page);
        return table;
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
        _updatesPage.Text = status?.Activity == HostUpdateActivity.Ready ? "Updates (ready)" : "Updates";
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
