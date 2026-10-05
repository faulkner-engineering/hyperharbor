using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Core.Updates;

/// <summary>The owner's update choices, saved in host-settings.json; they override the configured defaults.</summary>
public sealed record UpdatePreferences(
    string Channel,
    [property: JsonConverter(typeof(JsonStringEnumConverter<UpdateMode>))] UpdateMode Mode,
    string? MaintenanceTime);

/// <summary>The update settings in effect: <see cref="UpdateOptions"/> from configuration with the saved preferences applied.</summary>
public sealed class UpdateSettings(UpdateOptions defaults, HostSettingsStore settings)
{
    public UpdateOptions Current()
    {
        var effective = new UpdateOptions
        {
            Channel = defaults.Channel,
            Channels = defaults.Channels,
            AllowedHosts = defaults.AllowedHosts,
            MaxPackageBytes = defaults.MaxPackageBytes,
            Mode = defaults.Mode,
            CheckIntervalHours = defaults.CheckIntervalHours,
            IdleMinutes = defaults.IdleMinutes,
            MaintenanceTime = defaults.MaintenanceTime,
            MaintenanceWindowMinutes = defaults.MaintenanceWindowMinutes,
        };

        if (settings.UpdatePreferences is { } chosen)
        {
            if (defaults.Channels.ContainsKey(chosen.Channel))
            {
                effective.Channel = chosen.Channel;
            }

            effective.Mode = chosen.Mode;
            effective.MaintenanceTime = chosen.MaintenanceTime;
        }

        return effective;
    }

    /// <exception cref="ArgumentException">The channel is not configured, or the time is not HH:mm.</exception>
    public void Save(UpdatePreferences preferences)
    {
        if (!defaults.Channels.ContainsKey(preferences.Channel))
        {
            throw new ArgumentException($"\"{preferences.Channel}\" is not an update channel. Use one of: {string.Join(", ", defaults.Channels.Keys)}.", nameof(preferences));
        }

        if (preferences.MaintenanceTime is { } time && new UpdateOptions { MaintenanceTime = time }.ParsedMaintenanceTime is null)
        {
            throw new ArgumentException("The maintenance time must be HH:mm, for example 03:00.", nameof(preferences));
        }

        settings.SetUpdatePreferences(preferences);
    }
}
