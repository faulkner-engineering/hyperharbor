namespace HyperHarbor.Host.Tray;

/// <summary>
/// Shows the pairing PIN with a countdown until the request expires. The layout sizes itself
/// from its contents so it stays readable at any display scaling.
/// </summary>
internal sealed class PinForm : Form
{
    private readonly DateTimeOffset _expiresAt;
    private readonly Label _countdown;
    private readonly System.Windows.Forms.Timer _timer;

    public PinForm(string deviceName, string pin, DateTimeOffset expiresAt, Action onCancel)
    {
        _expiresAt = expiresAt;

        // Design at 96 DPI; WinForms scales fonts and padding to the monitor's DPI.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        Text = "HyperHarbor pairing";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 11f);
        Padding = new Padding(24);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
        };

        layout.Controls.Add(new Label
        {
            Text = $"Enter this PIN on \"{deviceName}\" to pair it with this PC:",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Margin = new Padding(0, 0, 0, 8),
        });

        layout.Controls.Add(new Label
        {
            Text = $"{pin[..3]} {pin[3..]}",
            Font = new Font("Consolas", 44f, FontStyle.Bold),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 8, 0, 8),
        });

        _countdown = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 16),
        };
        layout.Controls.Add(_countdown);

        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 2, 12, 2),
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.Cancel,
        };
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
