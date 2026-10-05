using Microsoft.Win32;

namespace HyperHarbor.Host.Tray;

/// <summary>The host states the notification area icon shows.</summary>
internal enum TrayIconState
{
    /// <summary>The host service is running and no session is connected.</summary>
    Awake,

    /// <summary>The host is asleep or the service is unreachable.</summary>
    Asleep,

    /// <summary>A session is connected.</summary>
    Connected,
}

/// <summary>
/// Supplies the notification area icon for the host state and the taskbar theme, and reports when it changes.
/// The icons are embedded resources copied from hyperharbor-brand/icons.
/// </summary>
internal sealed class TrayIconProvider : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Dictionary<string, Icon> _icons = new(StringComparer.Ordinal);
    private TrayIconState _state;
    private bool _lightTheme;
    private bool _disposed;

    public TrayIconProvider(TrayIconState initialState)
    {
        _state = initialState;
        _lightTheme = ReadTaskbarUsesLightTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
    public event EventHandler? IconChanged;

    /// <summary>The icon for the current state and taskbar theme. The provider owns it; do not dispose it.</summary>
    public Icon Current => Load(ResourceName(_state, _lightTheme));

    public TrayIconState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                IconChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The manifest resource name of the icon for a state and taskbar theme.</summary>
    internal static string ResourceName(TrayIconState state, bool lightTheme)
    {
        var name = state switch
        {
            TrayIconState.Awake => "tray-awake",
            TrayIconState.Asleep => "tray-asleep",
            TrayIconState.Connected => "tray-connected",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
        return $"HyperHarbor.Host.Tray.Resources.{name}{(lightTheme ? "-light-theme" : string.Empty)}.ico";
    }

    /// <summary>
    /// Reads SystemUsesLightTheme, which applies to the taskbar and notification area (AppsUseLightTheme applies to
    /// windows). A missing value means the dark taskbar that Windows uses by default.
    /// </summary>
    internal static bool ReadTaskbarUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A theme change arrives as the General category; reading the value again is cheap, so check on any change.
        var lightTheme = ReadTaskbarUsesLightTheme();
        if (!_disposed && lightTheme != _lightTheme)
        {
            _lightTheme = lightTheme;
            IconChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Icon Load(string resourceName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_icons.TryGetValue(resourceName, out var icon))
        {
            using var stream = typeof(TrayIconProvider).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The tray icon resource {resourceName} is missing.");

            // Pick the frame that matches the notification area's icon size at the current display scaling.
            icon = new Icon(stream, SystemInformation.SmallIconSize);
            _icons.Add(resourceName, icon);
        }

        return icon;
    }
}
