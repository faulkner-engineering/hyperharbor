using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>Sends keystrokes to a running VM's console.</summary>
public interface IVmKeyboard
{
    /// <summary>Presses and releases one key, by its Windows virtual-key code.</summary>
    Task TypeKeyAsync(Guid vmId, int virtualKey, CancellationToken cancellationToken);
}

/// <summary>
/// Msvm_Keyboard.TypeKey. One key per call: Msvm_Keyboard.TypeText can drop characters (see CLAUDE.md).
/// </summary>
public sealed class CimVmKeyboard : IVmKeyboard
{
    public Task TypeKeyAsync(Guid vmId, int virtualKey, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var keyboard = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_Keyboard WHERE SystemName = '{vmId:D}'")
                ?? throw new VmNotFoundException(vmId);
            using var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("keyCode", (uint)virtualKey, CimType.UInt32, CimFlags.In),
            };
            using var result = await HyperVCim.InvokeAsync(session, keyboard, "TypeKey", parameters, "TypeKey", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}
