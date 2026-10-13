using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.GameAware;

/// <summary>Pauses a VM in memory with Msvm_ComputerSystem.RequestStateChange (Paused, 32768) and resumes it (Enabled, 2).</summary>
public sealed class CimVmPauser : IVmPauser
{
    private const string Namespace = @"root\virtualization\v2";
    private const ushort RequestedStateEnabled = 2;
    private const ushort RequestedStatePaused = 32768;
    private const uint ReturnCompleted = 0;
    private const uint ReturnJobStarted = 4096;

    public Task PauseAsync(Guid vmId, CancellationToken cancellationToken) =>
        Task.Run(() => Request(vmId, RequestedStatePaused, "pausing the virtual machine"), cancellationToken);

    public Task ResumeAsync(Guid vmId, CancellationToken cancellationToken) =>
        Task.Run(() => Request(vmId, RequestedStateEnabled, "resuming the virtual machine"), cancellationToken);

    private static void Request(Guid vmId, ushort requestedState, string operation)
    {
        try
        {
            using var session = CimSession.Create(null);
            var systems = session.QueryInstances(Namespace, "WQL", $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'").ToList();
            try
            {
                var system = systems.FirstOrDefault() ?? throw new VmNotFoundException(vmId);
                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("RequestedState", requestedState, CimType.UInt16, CimFlags.In),
                };
                using var result = session.InvokeMethod(Namespace, system, "RequestStateChange", parameters);
                var code = Convert.ToUInt32(result.ReturnValue?.Value ?? uint.MaxValue, System.Globalization.CultureInfo.InvariantCulture);
                if (code is not (ReturnCompleted or ReturnJobStarted))
                {
                    throw new HyperVOperationException(operation, code);
                }
            }
            finally
            {
                foreach (var system in systems)
                {
                    system.Dispose();
                }
            }
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.InvalidNamespace or NativeErrorCode.AccessDenied)
        {
            throw new HyperVUnavailableException("Hyper-V cannot be reached to pause or resume a virtual machine.", ex);
        }
    }
}
