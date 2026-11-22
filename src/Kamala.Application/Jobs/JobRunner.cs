using Kamala.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Kamala.Application.Jobs;

/// <summary>
/// The lifecycle every tool shares: create a workspace, take in the uploads, do the work, publish the results.
/// A failed job is discarded immediately so nothing lingers on disk.
/// </summary>
public sealed class JobRunner(IJobStore store, FileIntake intake, IOptions<ProcessingLimits> limits)
{
    public delegate Task<IReadOnlyList<OutputFile>> Work(IReadOnlyList<InputFile> inputs, IJobWorkspace workspace, CancellationToken ct);

    public async Task<JobResult> RunAsync(IReadOnlyList<IncomingFile> files, Work work, CancellationToken ct)
    {
        var workspace = store.Create();
        try
        {
            var inputs = await intake.StoreAsync(files, workspace, ct);
            var outputs = await work(inputs, workspace, ct);
            return store.Publish(workspace, outputs);
        }
        catch
        {
            store.Discard(workspace);
            throw;
        }
    }

    /// <summary>Runs <paramref name="each"/> for every input with bounded parallelism, keeping the input order.</summary>
    public async Task<IReadOnlyList<T>> ForEachAsync<T>(IReadOnlyList<InputFile> inputs, Func<InputFile, CancellationToken, Task<T>> each, CancellationToken ct)
    {
        var results = new T[inputs.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, inputs.Count),
            new ParallelOptions { MaxDegreeOfParallelism = limits.Value.MaxParallelism, CancellationToken = ct },
            async (i, token) => results[i] = await each(inputs[i], token));
        return results;
    }
}
