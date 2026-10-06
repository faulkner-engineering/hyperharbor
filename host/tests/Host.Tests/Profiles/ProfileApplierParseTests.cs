using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class ProfileApplierParseTests
{
    [Fact]
    public void Items_AreRead_OneItemOrMany()
    {
        var many = PowerShellDirectProfileApplier.ParseItems(JsonNode.Parse("""
            { "items": [ { "item": "7-Zip", "ok": true }, { "item": "News", "ok": false, "error": " gone \r\n" }, { "item": "Feature", "ok": true, "restart": true } ] }
            """));
        var one = PowerShellDirectProfileApplier.ParseItems(JsonNode.Parse("""{ "items": { "item": "7-Zip", "ok": true } }"""));

        Assert.Equal(
            [new ApplyItemResult("7-Zip", true), new ApplyItemResult("News", false, "gone"), new ApplyItemResult("Feature", true, RestartNeeded: true)],
            many);
        Assert.Equal([new ApplyItemResult("7-Zip", true)], one);
        Assert.Empty(PowerShellDirectProfileApplier.ParseItems(JsonNode.Parse("{}")));
    }

    /// <summary>
    /// Seen live: a script that wrapped its argument array once too often reported every item name in one array.
    /// That is a guest problem for the job to report, not an unexpected host failure.
    /// </summary>
    [Theory]
    [InlineData("""{ "items": [ { "item": ["a", "b"], "ok": true } ] }""")]
    [InlineData("""{ "items": [ { "item": "a", "ok": "yes" } ] }""")]
    [InlineData("""[ 1, 2 ]""")]
    public void AnUnexpectedShape_IsAGuestError(string json)
    {
        Assert.Throws<GuestOperationException>(() => PowerShellDirectProfileApplier.ParseItems(JsonNode.Parse(json)));
    }
}
