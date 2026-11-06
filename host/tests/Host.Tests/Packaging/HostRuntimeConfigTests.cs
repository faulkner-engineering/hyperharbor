using System.Text.Json;

namespace HyperHarbor.Host.Tests.Packaging;

/// <summary>
/// Runtime settings HyperHarbor.Host.exe carries in its runtimeconfig.json (embedded in the single executable when
/// published). The test project references Host.Service, so the build copies that file beside the tests.
/// </summary>
public sealed class HostRuntimeConfigTests
{
    [Fact]
    public void DynamicPgo_IsOff()
    {
        // Phase 32 (docs/dotnet10-migration.md): dynamic PGO cost the .NET 10 host about 70 ms of startup.
        Assert.False(ConfigProperties().GetProperty("System.Runtime.TieredPGO").GetBoolean());
    }

    private static JsonElement ConfigProperties()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "HyperHarbor.Host.runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").Clone();
    }
}
