namespace HyperHarbor.Host.Core.Updates;

/// <summary>
/// Reports on the calling thread. <see cref="Progress{T}"/> queues each report to the thread pool, so a late download
/// report could arrive after the step that follows it.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
