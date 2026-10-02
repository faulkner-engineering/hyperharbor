namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Reads raw virtual machine data from Hyper-V.
/// </summary>
public interface IHyperVReader
{
    /// <exception cref="HyperVUnavailableException">Hyper-V cannot be queried.</exception>
    Task<HyperVSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
}
