using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.HostProfiles;
using HyperHarbor.Host.Core.Transfer;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

public class TpmCapabilityProbeTests
{
    private static JsonNode Answer(bool canOpenGrantedKeys) =>
        new JsonObject { ["canEnableTpm"] = true, ["canOpenGrantedKeys"] = canOpenGrantedKeys, ["detail"] = null };

    [Fact]
    public async Task ACallerThatGivesUp_DoesNotStopTheProbe_AndTheNextCallerGetsItsAnswer()
    {
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var probe = new PowerShellTpmCapabilityProbe(NullLogger<PowerShellTpmCapabilityProbe>.Instance, () =>
        {
            Interlocked.Increment(ref runs);
            return release.Task;
        });

        using (var impatient = new CancellationTokenSource())
        {
            var first = probe.ProbeAsync(impatient.Token);
            await impatient.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }

        release.SetResult(Answer(canOpenGrantedKeys: true));
        var answer = await probe.ProbeAsync(CancellationToken.None);

        Assert.True(answer.CanOpenGrantedKeys);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task CallersWhileAProbeRuns_ShareIt_AndLaterCallersGetTheKeptAnswer()
    {
        var release = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;
        var probe = new PowerShellTpmCapabilityProbe(NullLogger<PowerShellTpmCapabilityProbe>.Instance, () =>
        {
            Interlocked.Increment(ref runs);
            return release.Task;
        });

        var waiting = Enumerable.Range(0, 3).Select(_ => probe.ProbeAsync(CancellationToken.None)).ToList();
        release.SetResult(Answer(canOpenGrantedKeys: false));
        var answers = await Task.WhenAll(waiting);
        var later = await probe.ProbeAsync(CancellationToken.None);

        Assert.All(answers, answer => Assert.False(answer.CanOpenGrantedKeys));
        Assert.Same(answers[0], later);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task AProbeThatFailed_RunsAgainForTheNextCaller()
    {
        var runs = 0;
        var probe = new PowerShellTpmCapabilityProbe(NullLogger<PowerShellTpmCapabilityProbe>.Instance, () =>
            ++runs == 1
                ? Task.FromException<JsonNode?>(new HostLeanException("PowerShell could not start."))
                : Task.FromResult<JsonNode?>(Answer(canOpenGrantedKeys: true)));

        var failure = await Assert.ThrowsAsync<TransferException>(() => probe.ProbeAsync(CancellationToken.None));
        var answer = await probe.ProbeAsync(CancellationToken.None);

        Assert.Contains("PowerShell could not start.", failure.Message);
        Assert.True(answer.CanOpenGrantedKeys);
        Assert.Equal(2, runs);
    }
}
