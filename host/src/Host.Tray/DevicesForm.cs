using System.Globalization;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Lists paired devices and lets the user revoke them.
/// </summary>
internal sealed class DevicesForm : Form
{
    private readonly ListView _list;
    private readonly Button _remove;
    private readonly Func<Guid, Task> _removeDevice;

    public DevicesForm(Func<Guid, Task> removeDevice)
    {
        _removeDevice = removeDevice;

        Text = "HyperHarbor paired devices";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 320);
        MinimumSize = new Size(420, 240);
        Font = new Font("Segoe UI", 9.5f);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _list.Columns.Add("Device", 200);
        _list.Columns.Add("Paired", 150);
        _list.Columns.Add("Certificate", 180);
        _remove = new Button { Text = "Remove…", AutoSize = true, Enabled = false };
        _list.SelectedIndexChanged += (_, _) => _remove.Enabled = _list.SelectedItems.Count == 1;
        _remove.Click += async (_, _) => await RemoveSelectedAsync();

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = close;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(_remove);

        Controls.Add(_list);
        Controls.Add(buttons);
    }

    public void ShowDevices(IReadOnlyList<TrayDevice> devices)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var device in devices)
        {
            var item = new ListViewItem(device.Name) { Tag = device };
            item.SubItems.Add(device.PairedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
            item.SubItems.Add(FormatFingerprint(device.CertificateFingerprint));
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _remove.Enabled = false;
    }

    private async Task RemoveSelectedAsync()
    {
        if (_list.SelectedItems.Count != 1 || _list.SelectedItems[0].Tag is not TrayDevice device)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Remove \"{device.Name}\"? It will no longer be able to connect until it is paired again.",
            "Remove paired device",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer == DialogResult.Yes)
        {
            await _removeDevice(device.DeviceId);
        }
    }

    /// <summary>Shows the first 16 hex digits in groups of four, enough to tell devices apart.</summary>
    private static string FormatFingerprint(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, 4).Select(i => fingerprint.Substring(i * 4, 4)));
}
