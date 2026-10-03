using System.Globalization;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>Thrown when a Hyper-V job (Msvm_ConcreteJob) ends in an error.</summary>
public sealed class HyperVJobFailedException : Exception
{
    public HyperVJobFailedException(string operation, uint errorCode, string? description)
        : base(string.IsNullOrWhiteSpace(description)
            ? $"Hyper-V {operation} failed with error {errorCode}."
            : $"Hyper-V {operation} failed: {description.Trim()}")
    {
        Operation = operation;
        ErrorCode = errorCode;
    }

    public string Operation { get; }

    public uint ErrorCode { get; }
}

/// <summary>
/// Method calls on root\virtualization\v2 that may complete asynchronously. Hyper-V returns 4096 and a
/// Msvm_ConcreteJob reference for long operations; these helpers wait for the job and report its progress.
/// </summary>
internal static class HyperVCim
{
    public const string Namespace = @"root\virtualization\v2";
    public const string QueryDialect = "WQL";

    private const uint ReturnCompleted = 0;
    private const uint ReturnJobStarted = 4096;

    // Msvm_ConcreteJob.JobState values.
    private const ushort JobCompleted = 7;
    private const ushort JobTerminated = 8;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Invokes an instance method and waits for its job, if it started one.</summary>
    /// <param name="progress">Receives the job's PercentComplete while it runs.</param>
    /// <exception cref="HyperVOperationException">Hyper-V rejected the call.</exception>
    /// <exception cref="HyperVJobFailedException">The job ended in an error.</exception>
    public static async Task<CimMethodResult> InvokeAsync(
        CimSession session,
        CimInstance target,
        string method,
        CimMethodParametersCollection parameters,
        string operation,
        CancellationToken cancellationToken,
        Action<int>? progress = null)
    {
        var result = session.InvokeMethod(Namespace, target, method, parameters);
        await CompleteAsync(session, result, operation, cancellationToken, progress).ConfigureAwait(false);
        return result;
    }

    /// <summary>Waits for the job a method result started, or returns at once if it completed synchronously.</summary>
    public static async Task CompleteAsync(
        CimSession session,
        CimMethodResult result,
        string operation,
        CancellationToken cancellationToken,
        Action<int>? progress = null)
    {
        var code = ReturnCode(result);
        if (code == ReturnCompleted)
        {
            return;
        }

        if (code != ReturnJobStarted || result.OutParameters["Job"]?.Value is not CimInstance jobReference)
        {
            throw new HyperVOperationException(operation, code);
        }

        while (true)
        {
            using var job = session.GetInstance(Namespace, jobReference);
            var state = Convert.ToUInt16(job.CimInstanceProperties["JobState"]?.Value ?? (ushort)0, CultureInfo.InvariantCulture);
            if (job.CimInstanceProperties["PercentComplete"]?.Value is { } percent)
            {
                progress?.Invoke(Convert.ToInt32(percent, CultureInfo.InvariantCulture));
            }

            if (state == JobCompleted)
            {
                return;
            }

            if (state >= JobTerminated)
            {
                var errorCode = Convert.ToUInt32(job.CimInstanceProperties["ErrorCode"]?.Value ?? 0u, CultureInfo.InvariantCulture);
                throw new HyperVJobFailedException(operation, errorCode, job.CimInstanceProperties["ErrorDescription"]?.Value as string);
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public static uint ReturnCode(CimMethodResult result) =>
        Convert.ToUInt32(result.ReturnValue?.Value ?? uint.MaxValue, CultureInfo.InvariantCulture);

    public static List<CimInstance> Query(CimSession session, string query) =>
        session.QueryInstances(Namespace, QueryDialect, query).ToList();

    public static CimInstance? QuerySingle(CimSession session, string query)
    {
        var instances = Query(session, query);
        foreach (var extra in instances.Skip(1))
        {
            extra.Dispose();
        }

        return instances.FirstOrDefault();
    }

    public static CimInstance ManagementService(CimSession session) =>
        QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemManagementService")
            ?? throw new HyperVUnavailableException("The Hyper-V management service was not found. Enable the Hyper-V role on this host.");

    /// <summary>WQL string literal contents: backslashes and quotes escaped.</summary>
    public static string Escape(string value) => value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("'", @"\'", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="action"/> on a thread-pool thread with a new local session, and converts the
    /// common "Hyper-V missing or denied" errors.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<CimSession, Task<T>> action, CancellationToken cancellationToken) =>
        Task.Run(() => RunCoreAsync(action), cancellationToken);

    public static Task RunAsync(Func<CimSession, Task> action, CancellationToken cancellationToken) =>
        RunAsync<bool>(async session =>
        {
            await action(session).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static async Task<T> RunCoreAsync<T>(Func<CimSession, Task<T>> action)
    {
        using var session = CimSession.Create(null);
        try
        {
            return await action(session).ConfigureAwait(false);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.InvalidNamespace)
        {
            throw new HyperVUnavailableException("The Hyper-V management namespace was not found. Enable the Hyper-V role on this host.", ex);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.AccessDenied)
        {
            throw new HyperVUnavailableException("Access to Hyper-V was denied. Run elevated or add the account to the Hyper-V Administrators group.", ex);
        }
    }
}
