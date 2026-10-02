using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Service.Wake;

public enum WakeFixOutcome
{
    /// <summary>The service had administrator rights and applied the fixes itself.</summary>
    Applied,

    /// <summary>The tray is asking the user to approve the fixes with a UAC prompt.</summary>
    AwaitingApproval,
}

public sealed class WakeFixUnavailableException : Exception
{
    public WakeFixUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Applies Wake-on-LAN fixes directly when the service is elevated; otherwise asks the user at
/// the host to approve them through the tray, which runs an elevated helper.
/// </summary>
public sealed class WakeFixCoordinator
{
    private readonly IWakeEnvironmentReader _reader;
    private readonly IWakeFixApprover _approver;
    private readonly ILogger<WakeFixCoordinator> _logger;
    private readonly bool _isElevated;

    public WakeFixCoordinator(IWakeEnvironmentReader reader, IWakeFixApprover approver, ILogger<WakeFixCoordinator> logger)
        : this(reader, approver, logger, Environment.IsPrivilegedProcess)
    {
    }

    internal WakeFixCoordinator(IWakeEnvironmentReader reader, IWakeFixApprover approver, ILogger<WakeFixCoordinator> logger, bool isElevated)
    {
        _reader = reader;
        _approver = approver;
        _logger = logger;
        _isElevated = isElevated;
    }

    /// <exception cref="InvalidWakeRequestException">No check IDs, or a check cannot be fixed automatically.</exception>
    /// <exception cref="WakeFixUnavailableException">No tray is connected to ask for approval.</exception>
    public async Task<(WakeFixOutcome Outcome, WakeReadiness? Readiness)> RequestAsync(
        IReadOnlyList<string> checkIds,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        var unknown = checkIds.Where(id => !WakeCheckIds.Fixable.Contains(id)).ToList();
        if (checkIds.Count == 0 || unknown.Count > 0)
        {
            throw new InvalidWakeRequestException(checkIds.Count == 0 ? "Specify at least one check to fix." : $"These checks cannot be fixed automatically: {string.Join(", ", unknown)}.");
        }

        var environment = await _reader.ReadAsync(cancellationToken);
        if (_isElevated)
        {
            foreach (var result in WakeFixer.Apply(environment, checkIds))
            {
                _logger.LogInformation("Wake fix {CheckId}: {Applied} ({Detail}).", result.CheckId, result.Applied, result.Detail);
            }

            return (WakeFixOutcome.Applied, WakeReadinessEvaluator.Evaluate(await _reader.ReadAsync(cancellationToken)));
        }

        if (!_approver.CanRequestApproval)
        {
            throw new WakeFixUnavailableException("Fixing these settings needs approval at the host. Open the HyperHarbor tray app on the host and try again.");
        }

        var titles = WakeReadinessEvaluator.Evaluate(environment).Checks.ToDictionary(check => check.Id, check => check.Title);
        var request = new WakeFixRequestedMessage(
            Guid.NewGuid(),
            requestedBy,
            checkIds.Distinct().Select(id => new WakeFixItem(id, titles.GetValueOrDefault(id, id))).ToList());

        _logger.LogInformation("Asking the tray to approve wake fixes {CheckIds} for {Device}.", string.Join(", ", checkIds), requestedBy);
        _approver.RequestApproval(request);
        return (WakeFixOutcome.AwaitingApproval, null);
    }

    public void OnCompleted(WakeFixCompletedMessage completed)
    {
        _logger.LogInformation("Wake fix request {RequestId} {Outcome}. {Detail}", completed.RequestId, completed.Outcome, completed.Detail);
    }
}
