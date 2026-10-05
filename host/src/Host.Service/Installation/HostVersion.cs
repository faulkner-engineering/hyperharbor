using System.Reflection;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>The version of this executable, from Directory.Build.props.</summary>
internal static class HostVersion
{
    public static SemanticVersion Current { get; } = SemanticVersion.Parse(
        typeof(HostVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
}
