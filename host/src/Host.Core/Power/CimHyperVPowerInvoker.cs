using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Performs power actions through root\virtualization\v2.
/// Start, turn off, and save use Msvm_ComputerSystem.RequestStateChange.
/// Shutdown and restart ask the guest through Msvm_ShutdownComponent.
/// </summary>
public sealed class CimHyperVPowerInvoker : IHyperVPowerInvoker
{
    private const string Namespace = @"root\virtualization\v2";
    private const string QueryDialect = "WQL";
    private const string ShutdownReason = "Requested by HyperHarbor";

    // RequestStateChange RequestedState values.
    private const ushort RequestedStateEnabled = 2;
    private const ushort RequestedStateDisabled = 3;
    private const ushort RequestedStateOffline = 6;

    // Common Hyper-V method return codes.
    private const uint ReturnCompleted = 0;
    private const uint ReturnJobStarted = 4096;
    private const uint ReturnNotSupported = 32770;
    private const uint ReturnInvalidState = 32775;

    private const ushort OperationalStatusOk = 2;

    public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
    {
        return Task.Run(() => InvokeCoreAsync(vmId, action, cancellationToken), cancellationToken);
    }

    private static async Task InvokeCoreAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
    {
        using var session = CimSession.Create(null);

        try
        {
            switch (action)
            {
                case VmAction.Start:
                    // Start runs as a job that fails when the host cannot give the VM its memory; wait so the
                    // caller gets Hyper-V's reason instead of a VM that silently stays off.
                    await RequestStateChangeAsync(session, vmId, action, RequestedStateEnabled, cancellationToken).ConfigureAwait(false);
                    break;
                case VmAction.TurnOff:
                    await RequestStateChangeAsync(session, vmId, action, RequestedStateDisabled, null).ConfigureAwait(false);
                    break;
                case VmAction.Save:
                    await RequestStateChangeAsync(session, vmId, action, RequestedStateOffline, null).ConfigureAwait(false);
                    break;
                case VmAction.Shutdown:
                    InvokeShutdownComponent(session, vmId, action, "InitiateShutdown");
                    break;
                case VmAction.Restart:
                    InvokeShutdownComponent(session, vmId, action, "InitiateReboot");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported action.");
            }
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.InvalidNamespace)
        {
            throw new HyperVUnavailableException(
                "The Hyper-V management namespace was not found. Enable the Hyper-V role on this host.", ex);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.AccessDenied)
        {
            throw new HyperVUnavailableException(
                "Access to Hyper-V was denied. Run elevated or add the account to the Hyper-V Administrators group.", ex);
        }
    }

    private static async Task RequestStateChangeAsync(
        CimSession session,
        Guid vmId,
        VmAction action,
        ushort requestedState,
        CancellationToken? waitForJob)
    {
        using var system = QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
            ?? throw new VmNotFoundException(vmId);

        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("RequestedState", requestedState, CimType.UInt16, CimFlags.In),
        };

        using var result = session.InvokeMethod(Namespace, system, "RequestStateChange", parameters);
        EnsureAccepted(action, "RequestStateChange", result);

        if (waitForJob is { } token && GetReturnCode(result) == ReturnJobStarted)
        {
            await HyperVCim.CompleteAsync(session, result, "starting the virtual machine", token).ConfigureAwait(false);
        }
    }

    private static void InvokeShutdownComponent(CimSession session, Guid vmId, VmAction action, string methodName)
    {
        using var component = QuerySingle(session, $"SELECT * FROM Msvm_ShutdownComponent WHERE SystemName = '{vmId:D}'")
            ?? throw new VmActionNotAllowedException(
                action,
                null,
                "The guest shutdown integration service is not available. Use turnOff to force the virtual machine off.");

        // OperationalStatus 2 (OK) means the guest is responding. Without a running guest OS it reports 12 (No Contact).
        var status = component.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];
        if (status is not { Length: > 0 } || status[0] != OperationalStatusOk)
        {
            throw new VmActionNotAllowedException(
                action,
                null,
                "The guest is not responding to the shutdown integration service. Wait for the guest OS to finish starting, or use turnOff.");
        }

        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Force", false, CimType.Boolean, CimFlags.In),
            CimMethodParameter.Create("Reason", ShutdownReason, CimType.String, CimFlags.In),
        };

        using var result = session.InvokeMethod(Namespace, component, methodName, parameters);
        if (GetReturnCode(result) == ReturnNotSupported)
        {
            throw new VmActionNotAllowedException(action, null, $"The guest does not support {action} through integration services.");
        }

        EnsureAccepted(action, methodName, result);
    }

    private static void EnsureAccepted(VmAction action, string operation, CimMethodResult result)
    {
        var returnCode = GetReturnCode(result);
        switch (returnCode)
        {
            case ReturnCompleted:
            case ReturnJobStarted:
                return;
            case ReturnInvalidState:
                throw new VmActionNotAllowedException(action, null, $"Hyper-V reported that {action} is not valid in the current state.");
            default:
                throw new HyperVOperationException(operation, returnCode);
        }
    }

    private static uint GetReturnCode(CimMethodResult result)
    {
        return Convert.ToUInt32(result.ReturnValue?.Value ?? uint.MaxValue, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CimInstance? QuerySingle(CimSession session, string query)
    {
        var instances = session.QueryInstances(Namespace, QueryDialect, query).ToList();
        foreach (var extra in instances.Skip(1))
        {
            extra.Dispose();
        }

        return instances.FirstOrDefault();
    }
}
