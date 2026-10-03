using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Sets the admin passphrase that paired devices enter before deleting, creating, or reconfiguring
/// VMs. The passphrase is hashed here and only the hash is sent to the service, so it never leaves
/// this process. The layout sizes itself from its contents so it stays readable at any display scaling.
/// </summary>
internal sealed class AdminPassphraseForm : Form
{
    private readonly TextBox _passphrase;
    private readonly TextBox _confirm;
    private readonly Label _error;
    private readonly Button _save;
    private readonly Func<SetAdminPassphraseMessage, Task> _send;

    public AdminPassphraseForm(bool replacing, Func<SetAdminPassphraseMessage, Task> send)
    {
        _send = send;

        // Design at 96 DPI; WinForms scales fonts and padding to the monitor's DPI.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Text = replacing ? "Change HyperHarbor admin passphrase" : "Set HyperHarbor admin passphrase";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(20);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
        };

        layout.Controls.Add(new Label
        {
            Text = "Paired devices must enter this passphrase before they can delete, create, or reconfigure " +
                "virtual machines, or force one off. " +
                (replacing ? "Changing it ends every device's current elevation." : string.Empty),
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 12),
        });

        _passphrase = AddField(layout, $"Passphrase (at least {AdminPassphrase.MinimumLength} characters):");
        _confirm = AddField(layout, "Confirm passphrase:");

        _error = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.Firebrick,
            Margin = new Padding(0, 0, 0, 8),
            Visible = false,
        };
        layout.Controls.Add(_error);

        _save = new Button { Text = "Save", AutoSize = true, Padding = new Padding(12, 2, 12, 2) };
        _save.Click += async (_, _) => await SaveAsync();
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(12, 2, 12, 2), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 4, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);
        layout.Controls.Add(buttons);

        AcceptButton = _save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    private static TextBox AddField(TableLayoutPanel layout, string label)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 2) });
        var box = new TextBox
        {
            UseSystemPasswordChar = true,
            MaxLength = AdminPassphrase.MaximumLength,
            Width = 420,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(box);
        return box;
    }

    private async Task SaveAsync()
    {
        var passphrase = _passphrase.Text;
        var problem = AdminPassphrase.Validate(passphrase)
            ?? (passphrase == _confirm.Text ? null : "The passphrases do not match.");
        if (problem is not null)
        {
            _error.Text = problem;
            _error.Visible = true;
            return;
        }

        _save.Enabled = false;
        _error.Visible = false;
        UseWaitCursor = true;
        try
        {
            // PBKDF2 with 600,000 iterations takes a moment; keep the window responsive.
            var hash = await Task.Run(() => AdminPassphrase.CreateHash(passphrase));
            await _send(hash);
        }
        finally
        {
            UseWaitCursor = false;
        }

        _passphrase.Clear();
        _confirm.Clear();
        DialogResult = DialogResult.OK;
        Close();
    }
}
