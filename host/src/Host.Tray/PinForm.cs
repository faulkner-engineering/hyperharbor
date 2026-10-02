namespace HyperHarbor.Host.Tray;

/// <summary>
/// Shows the pairing PIN with a countdown until the request expires.
/// </summary>
internal sealed class PinForm : Form
{
    private readonly DateTimeOffset _expiresAt;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer;

    public PinForm(string deviceName, string pin, DateTimeOffset expiresAt, Action onCancel)
    {
        _expiresAt = expiresAt;

        Text = "HyperHarbor pairing";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(380, 230);
        Font = new Font("Segoe UI", 10f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 4,
        };

        layout.Controls.Add(new Label
        {
            Text = $"Enter this PIN on \"{deviceName}\" to pair it with this PC.",
            AutoSize = true,
            MaximumSize = new Size(340, 0),
        });

        layout.Controls.Add(new Label
        {
            Text = $"{pin[..3]} {pin[3..]}",
            Font = new Font("Segoe UI", 32f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 12, 0, 8),
        });

        _countdown = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        layout.Controls.Add(_countdown);

        var cancel = new Button { Text = "Cancel", AutoSize = true, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => onCancel();
        layout.Controls.Add(cancel);
        CancelButton = cancel;

        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateCountdown();
        UpdateCountdown();
        _timer.Start();
    }

    public Guid PairingId { get; init; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateCountdown()
    {
        var remaining = _expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            Close();
            return;
        }

        _countdown.Text = $"Expires in {(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }
}
