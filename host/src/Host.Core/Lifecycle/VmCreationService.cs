using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// Creates Generation 2 VMs that boot from an ISO in the library. The request is validated against
/// the host first; the Hyper-V steps run as a job. If a step fails, the job removes the VM and the
/// disk it created, so no half-built VM is left behind. While a job runs, the VM's notes say so.
/// </summary>
public sealed class VmCreationService
{
    public const int MaxNameLength = 100;
    public const int MinDiskSizeGb = 1;

    /// <summary>The largest VHDX Hyper-V supports, 64 TB.</summary>
    public const int MaxDiskSizeGb = 64 * 1024;

    public const string IncompleteNote = "HyperHarbor: creation in progress. If this VM remains after the job ends, it can be deleted.";

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    private readonly IVmInventory _inventory;
    private readonly IHyperVBuilder _builder;
    private readonly IHyperVStorage _storage;
    private readonly IHyperVHost _hyperV;
    private readonly IHostCapacityReader _capacity;
    private readonly IDiskFiles _files;
    private readonly IsoLibrary _isos;
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options;
    private readonly VmStorageLocation _location;
    private readonly ILogger<VmCreationService> _logger;
    private readonly HashSet<string> _namesInProgress = new(StringComparer.OrdinalIgnoreCase);

    public VmCreationService(
        IVmInventory inventory,
        IHyperVBuilder builder,
        IHyperVStorage storage,
        IHyperVHost hyperV,
        IHostCapacityReader capacity,
        IDiskFiles files,
        IsoLibrary isos,
        VmJobStore jobs,
        LifecycleOptions options,
        ILogger<VmCreationService> logger,
        VmStorageLocation? location = null)
    {
        _location = location ?? new VmStorageLocation(null, options, hyperV);
        _inventory = inventory;
        _builder = builder;
        _storage = storage;
        _hyperV = hyperV;
        _capacity = capacity;
        _files = files;
        _isos = isos;
        _jobs = jobs;
        _options = options;
        _logger = logger;
    }

    /// <exception cref="LifecycleValidationException">The request is invalid; Errors lists each problem.</exception>
    /// <exception cref="ResourceWarningsException">Warnings were not acknowledged.</exception>
    /// <exception cref="LifecycleConflictException">A VM with this name is already being created.</exception>
    public async Task<VmJobSnapshot> StartAsync(Guid userId, CreateVmRequest request, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var blueprint = await PlanAsync(request, cancellationToken).ConfigureAwait(false);

        lock (_namesInProgress)
        {
            if (!_namesInProgress.Add(blueprint.Name))
            {
                throw new LifecycleConflictException($"A virtual machine named \"{blueprint.Name}\" is already being created.");
            }
        }

        try
        {
            return _jobs.Start(VmJobKind.CreateVm, null, userId, "Creating the virtual hard disk", context => RunAsync(blueprint, request, context), job =>
            {
                lock (_namesInProgress)
                {
                    _namesInProgress.Remove(blueprint.Name);
                }

                onFinished?.Invoke(job);
            });
        }
        catch
        {
            lock (_namesInProgress)
            {
                _namesInProgress.Remove(blueprint.Name);
            }

            throw;
        }
    }

    /// <summary>Validates the request and works out the folders, ISO path, and switch.</summary>
    internal async Task<VmBlueprint> PlanAsync(CreateVmRequest request, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var name = (request.Name ?? string.Empty).Trim();

        CheckName(name, errors);
        var existing = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new("name", $"A virtual machine named \"{name}\" already exists."));
        }

        string? isoPath = null;
        try
        {
            isoPath = _isos.Resolve(request.IsoName ?? string.Empty);
        }
        catch (LifecycleValidationException ex)
        {
            errors.AddRange(ex.Errors);
        }

        if (request.DiskSizeGb is < MinDiskSizeGb or > MaxDiskSizeGb)
        {
            errors.Add(new("diskSizeGb", $"Use between {MinDiskSizeGb} and {MaxDiskSizeGb} GB."));
        }

        var host = _capacity.Read();
        VmSettingsValidator.Check(
            new ComputeRequest(request.ProcessorCount, request.StartupMemoryMb, request.MaximumMemoryMb, request.DynamicMemory),
            host,
            _options.HostMemoryReserveMb,
            errors,
            warnings);

        var switches = await _hyperV.ListSwitchesAsync(cancellationToken).ConfigureAwait(false);
        string? switchId;
        if (request.SwitchId is { Length: > 0 } requested)
        {
            switchId = switches.FirstOrDefault(item => string.Equals(item.Id, requested, StringComparison.OrdinalIgnoreCase))?.Id;
            if (switchId is null)
            {
                errors.Add(new("switchId", "The virtual switch was not found on the host."));
            }
        }
        else
        {
            switchId = switches.FirstOrDefault(item => item.IsDefault)?.Id;
            if (switchId is null)
            {
                warnings.Add(new("switchId", "The host has no Default Switch, so the VM will have no network adapter. Choose a switch to connect it."));
            }
        }

        string? configurationFolder = null;
        string diskPath = string.Empty;
        if (errors.All(error => error.Field != "name"))
        {
            (configurationFolder, diskPath) = await FoldersAsync(name, cancellationToken).ConfigureAwait(false);
            if (_files.Exists(diskPath))
            {
                errors.Add(new("name", $"A disk already exists at {diskPath}. Choose another name or remove the file."));
            }
            else if (configurationFolder is not null && Directory.Exists(configurationFolder) && Directory.EnumerateFileSystemEntries(configurationFolder).Any())
            {
                errors.Add(new("name", $"The folder {configurationFolder} already exists and is not empty. Choose another name."));
            }
            else if (_files.AvailableSpaceMb(diskPath) is { } freeMb && freeMb < (long)request.DiskSizeGb * 1024)
            {
                warnings.Add(new("diskSizeGb",
                    $"The disk can grow to {request.DiskSizeGb} GB, but its drive has {VmSettingsValidator.Mb(freeMb)} free."));
            }
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        if (warnings.Count > 0 && !request.AcknowledgeWarnings)
        {
            throw new ResourceWarningsException(warnings);
        }

        return new VmBlueprint(
            name,
            configurationFolder,
            diskPath,
            isoPath!,
            request.ProcessorCount,
            request.StartupMemoryMb,
            request.DynamicMemory ? request.MaximumMemoryMb : request.StartupMemoryMb,
            request.DynamicMemory,
            switchId);
    }

    private async Task RunAsync(VmBlueprint blueprint, CreateVmRequest request, VmJobContext context)
    {
        var diskCreated = false;
        Guid? vmId = null;
        try
        {
            await _builder.CreateDiskAsync(blueprint.DiskPath, (long)request.DiskSizeGb * 1024 * 1024 * 1024,
                percent => context.Report("Creating the virtual hard disk", percent * 30 / 100), context.Stopping).ConfigureAwait(false);
            diskCreated = true;

            context.Report("Defining the virtual machine", 35);
            vmId = await _builder.DefineAsync(blueprint, IncompleteNote, context.Stopping).ConfigureAwait(false);
            context.AttachVm(vmId.Value);

            context.Report("Adding processors, memory, disk, DVD, and network", 50);
            await _builder.ConfigureAsync(vmId.Value, blueprint, context.Stopping).ConfigureAwait(false);

            if (request.EnableTpm)
            {
                context.Report("Adding a virtual TPM", 80);
                await _builder.EnableTpmAsync(vmId.Value, context.Stopping).ConfigureAwait(false);
            }

            context.Report("Finishing", 95);
            await _builder.SetNotesAsync(vmId.Value, string.Empty, context.Stopping).ConfigureAwait(false);
            _logger.LogInformation("Created virtual machine {Name} ({VmId}).", blueprint.Name, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Creating {Name} failed; removing what was created.", blueprint.Name);
            await RollBackAsync(blueprint, vmId, diskCreated).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Removes the VM and the disk this job created. Never touches anything that existed before.</summary>
    private async Task RollBackAsync(VmBlueprint blueprint, Guid? vmId, bool diskCreated)
    {
        if (vmId is { } id)
        {
            try
            {
                await _storage.DeleteVmAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not remove the partly created VM {Name} ({VmId}). Delete it in Hyper-V Manager.", blueprint.Name, id);
            }
        }

        if (diskCreated)
        {
            try
            {
                _files.Delete(blueprint.DiskPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not remove the disk {Path} created for {Name}.", blueprint.DiskPath, blueprint.Name);
            }
        }
    }

    private async Task<(string? ConfigurationFolder, string DiskPath)> FoldersAsync(string name, CancellationToken cancellationToken)
    {
        var folders = await _location.ForAsync(name, cancellationToken).ConfigureAwait(false);
        return (folders.ConfigurationFolder, folders.DiskPath);
    }

    private static void CheckName(string name, List<ValidationIssue> errors)
    {
        if (name.Length == 0 || name.Length > MaxNameLength)
        {
            errors.Add(new("name", $"Use a name of 1 to {MaxNameLength} characters."));
        }
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
        {
            errors.Add(new("name", "The name cannot contain \\ / : * ? \" < > | or end with a period."));
        }
        else if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name), StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new("name", $"\"{name}\" is reserved by Windows. Choose another name."));
        }
    }
}
