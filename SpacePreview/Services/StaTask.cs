using System.Collections.Concurrent;

namespace SpacePreview.Services;

/// <summary>
/// Runs work on one STA thread. Shell COM identity plumbing
/// (IServiceProvider.QueryService for the per-tab browser) returns nothing on
/// MTA thread-pool threads, so selection lookups cannot use Task.Run.
/// </summary>
internal static class StaTask
{
    private static readonly BlockingCollection<IWorkItem> Queue = new();
    private static readonly Thread Worker = StartWorker();

    public static Task<T> Run<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add(new WorkItem<T>(work, completion));
        return completion.Task;
    }

    private static Thread StartWorker()
    {
        var thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SpacePreview shell STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void WorkerLoop()
    {
        foreach (var item in Queue.GetConsumingEnumerable())
        {
            item.Run();
        }
    }

    private interface IWorkItem
    {
        void Run();
    }

    private sealed class WorkItem<T>(Func<T> work, TaskCompletionSource<T> completion) : IWorkItem
    {
        public void Run()
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
    }
}
