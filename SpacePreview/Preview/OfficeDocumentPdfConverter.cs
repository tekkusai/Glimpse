using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// A converted document: a PDF, or a spreadsheet's HTML page (with one page
/// per sheet when it has several). A partial holds only the first pages of a
/// big document; its job's Complete brings the rest.
/// </summary>
internal sealed record ConvertedOfficeDocument(
    string DocumentPath,
    IReadOnlyList<SpreadsheetSheet>? Sheets = null,
    int PartialPages = 0,
    int ExpectedPages = 0)
{
    public bool IsPartial => PartialPages > 0;
}

internal sealed record OfficeProgressiveConversionPlan(int FirstPageCount, int ExpectedPageCount, string Filter);

/// <summary>What one engine run is asked to do. The runner writes into OutputDirectory.</summary>
internal sealed record OfficeRunRequest(string SourcePath, string ConvertTo, string OutputDirectory, OfficeRunControl Control);

/// <summary>
/// The running engine as the lane steers it: its priority, how long it may
/// take, and its end. soffice.exe is only a launcher, so all three have to
/// reach the soffice.bin it starts. Work no preview is blocked on (the rest
/// of a document whose first pages show, a neighbor's warm-up) drops to
/// BelowNormal and gets the long limit.
/// </summary>
internal sealed class OfficeRunControl
{
    // A preview gives up and shows the text-only page. Work nobody is
    // blocked on is slow by design, so its limit only stops a stuck engine.
    private static readonly TimeSpan ForegroundLimit = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan BackgroundLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EndPatience = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Process? _process;
    private ChildJob? _job;
    private bool _inJob;
    private CancellationTokenSource? _limit;
    private long _started;

    public bool Background { get; private set; }

    public void SetBackground(bool background)
    {
        lock (_gate)
        {
            if (Background == background)
            {
                return;
            }

            Background = background;
            ApplyPriority();
            ApplyLimit();
        }
    }

    /// <summary>The run begins: <paramref name="limit"/> is cancelled when its time is up.</summary>
    public void StartClock(CancellationTokenSource limit)
    {
        lock (_gate)
        {
            _limit = limit;
            _started = Stopwatch.GetTimestamp();
            ApplyLimit();
        }
    }

    /// <summary>
    /// Takes the started engine into a job of its own, which ends it with
    /// the run, and with the app however the app ends.
    /// </summary>
    public void Attach(Process process)
    {
        lock (_gate)
        {
            _process = process;
            _job = new ChildJob();
            _inJob = _job.Add(process);
            if (_inJob)
            {
                // A soffice.bin the launcher started before the job took
                // the launcher is outside it; whatever starts later is in.
                foreach (var child in ProcessTree.ChildrenOf(process))
                {
                    using (child)
                    {
                        _job.Add(child);
                    }
                }
            }
            else
            {
                DiagnosticLog.Write("office engine is outside its job");
            }

            if (Background)
            {
                ApplyPriority();
            }
        }
    }

    /// <summary>
    /// Ends whatever the run started and returns once it is gone. A live
    /// soffice.bin holds the profile the next run needs, and the lu*.tmp
    /// files in the staging folder that is deleted next.
    /// </summary>
    public void End()
    {
        ChildJob? job;
        Process? process;
        bool inJob;
        lock (_gate)
        {
            job = _job;
            process = _process;
            inJob = _inJob;
            _job = null;
            _process = null;
            _limit = null;
        }

        if (job is null || process is null)
        {
            return;
        }

        using (job)
        {
            // A launcher that exited by itself waited for soffice.bin first.
            // One that is stopped leaves soffice.bin to wait for: the job's
            // own count is zero before its processes have closed their files.
            var engine = new List<Process>();
            try
            {
                if (!process.HasExited)
                {
                    engine = ProcessTree.ChildrenOf(process);
                    if (!inJob)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }

                job.Terminate();
                var clock = Stopwatch.StartNew();
                foreach (var part in engine.Prepend(process))
                {
                    var left = EndPatience - clock.Elapsed;
                    if (left <= TimeSpan.Zero || !part.WaitForExit(left))
                    {
                        DiagnosticLog.Write("office engine still running after its run ended");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"office engine end failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                engine.ForEach(part => part.Dispose());
            }
        }
    }

    /// <summary>
    /// How long a run that has taken <paramref name="elapsed"/> may still
    /// take. A preview waits the foreground limit from the moment it starts
    /// waiting, also on a run that began as a warm-up.
    /// </summary>
    internal static TimeSpan TimeLeft(bool background, TimeSpan elapsed)
    {
        var left = BackgroundLimit - elapsed;
        return background || left < ForegroundLimit ? left : ForegroundLimit;
    }

    private void ApplyLimit()
    {
        if (_limit is null)
        {
            return;
        }

        var left = TimeLeft(Background, Stopwatch.GetElapsedTime(_started));
        try
        {
            _limit.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the lane's decision and this.
        }
    }

    private void ApplyPriority()
    {
        if (_process is null)
        {
            return;
        }

        var priority = Background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        try
        {
            _process.PriorityClass = priority;
            foreach (var child in ProcessTree.ChildrenOf(_process))
            {
                using (child)
                {
                    child.PriorityClass = priority;
                }
            }
        }
        catch
        {
            // Priority is a courtesy; the run stays killable either way.
        }
    }

    /// <summary>Direct children of a process, from one Toolhelp snapshot.</summary>
    private static class ProcessTree
    {
        private const uint SnapProcess = 0x00000002;

        /// <summary>
        /// Windows reuses process ids, so a stranger can name a dead parent
        /// that had this one. Only a process started after
        /// <paramref name="parent"/> is its child.
        /// </summary>
        public static List<Process> ChildrenOf(Process parent)
        {
            var children = new List<Process>();
            try
            {
                var born = parent.StartTime;
                foreach (var childId in ChildIdsOf(parent.Id))
                {
                    Process? child = null;
                    try
                    {
                        child = Process.GetProcessById(childId);
                        if (child.StartTime >= born)
                        {
                            children.Add(child);
                            child = null;
                        }
                    }
                    catch
                    {
                        // Gone already, or another user's: not ours either way.
                    }
                    finally
                    {
                        child?.Dispose();
                    }
                }
            }
            catch
            {
                // The parent has exited; nothing is left to find.
            }

            return children;
        }

        private static List<int> ChildIdsOf(int parentId)
        {
            var children = new List<int>();
            var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return children;
            }

            try
            {
                var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
                {
                    if (entry.ParentProcessId == parentId)
                    {
                        children.Add((int)entry.ProcessId);
                    }
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return children;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

/// <summary>
/// One conversion, keyed by its cache entry, shared by every preview of the
/// file and by the neighbor warm-up. It runs while one of them still waits
/// for it. When the last one leaves, the engine stops, except for the rest
/// of a document whose first pages a preview showed: a run already under way
/// lands, so the next look is the whole document. Finished first pages stay
/// cached, so a revisit shows them at once and converts only the rest.
/// </summary>
internal sealed class OfficeConversionJob
{
    private readonly TaskCompletionSource<ConvertedOfficeDocument?> _first =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<ConvertedOfficeDocument?> _complete =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal OfficeConversionJob(string key, string sourcePath, OfficeKind kind)
    {
        Key = key;
        SourcePath = sourcePath;
        Kind = kind;
    }

    public string Key { get; }
    public string SourcePath { get; }
    public OfficeKind Kind { get; }

    /// <summary>The first thing worth showing: the document, or its first pages when it is big.</summary>
    public Task<ConvertedOfficeDocument?> FirstContent => _first.Task;

    /// <summary>The whole document, or null when it could not be converted.</summary>
    public Task<ConvertedOfficeDocument?> Complete => _complete.Task;

    // Guarded by the converter's lane lock.
    internal int Waiters;
    // Previews, as opposed to the neighbor warm-up; only they make work urgent.
    internal int ForegroundWaiters;
    internal bool RestStarted;
    // A preview showed the first pages while the rest converts.
    internal bool Finishing;
    internal bool Finished;
    // The whole document reached the cache, so its first pages can go.
    internal bool Landed;
    internal string? PartialDirectory;
    internal int PartialHolds;
    internal readonly List<CancellationTokenRegistration> Registrations = [];

    /// <summary>
    /// A viewer showing the partial keeps its folder alive until it lets go.
    /// False when the whole document replaced it; show Complete instead.
    /// </summary>
    public bool TryHoldPartial() => OfficeDocumentPdfConverter.TryHoldPartial(this);

    public void ReleasePartial() => OfficeDocumentPdfConverter.ReleasePartial(this);

    internal void PublishFirst(ConvertedOfficeDocument document) => _first.TrySetResult(document);

    internal void PublishComplete(ConvertedOfficeDocument? document)
    {
        _first.TrySetResult(document);
        _complete.TrySetResult(document);
    }
}

internal static class OfficeDocumentPdfConverter
{
    private const int ProgressiveFirstPages = 6;
    private const int ProgressivePresentationSlideThreshold = 10;
    private const int ProgressiveDocumentPageThreshold = 25;
    private const long ProgressiveDocumentSizeThreshold = 1_500_000;
    private const long MaxAppPropertiesBytes = 256 * 1024;
    private const string BlockedProxyHost = "127.0.0.1";
    private const int BlockedProxyPort = 9;

    // The pipeline version is the cache's invalidation: bump it whenever
    // conversion or sheet styling changes, or old output keeps being served.
    private const string PipelineVersion = "v15";

    // The cache key of a big document's first pages, beside its whole-document formats.
    private const string PartialFormat = "first-pages";

    // A conversion the engine could not finish leaves this file in the
    // document's cache entry, and is not tried again for a day. The entry
    // is keyed by the file's length and time, so a changed file is tried
    // at once; the day is for what passes, like a machine that slept.
    private const string FailureMark = "failed";
    private static readonly TimeSpan FailureMemory = TimeSpan.FromDays(1);

    private static readonly XNamespace OfficeRegistry = "http://openoffice.org/2001/registry";

    // One engine at a time on one private profile: a second soffice on the
    // same profile forwards its request to the first and both fail.
    private static readonly object Lane = new();
    private static readonly object PriorityGate = new();
    private static readonly Dictionary<string, OfficeConversionJob> Jobs = new(StringComparer.Ordinal);
    private static readonly List<LaneTicket> Waiting = [];
    private static LaneTicket? _holder;
    private static string? _soffice;

    /// <summary>The engine run. Tests swap in a fake; production runs soffice.</summary>
    internal static Func<OfficeRunRequest, CancellationToken, string?> Runner { get; set; } = RunSoffice;

    public static bool IsAvailable => FindSoffice() is not null;

    /// <summary>The engine's display name, for "Converting with LibreOffice". Null without one.</summary>
    public static string? EngineName => FindSoffice() is { } path
        ? path.Contains("OpenOffice", StringComparison.OrdinalIgnoreCase) ? "OpenOffice" : "LibreOffice"
        : null;

    /// <summary>A finished conversion on disk, without touching the engine lane.</summary>
    public static ConvertedOfficeDocument? TryGetCached(string path) =>
        TryGetCached(path, FileKinds.GetOfficeKind(path));

    public static ConvertedOfficeDocument? TryGetCached(string path, OfficeKind kind)
    {
        foreach (var format in Formats(kind))
        {
            try
            {
                var directory = GetCacheDirectory(path, format);
                var output = Path.Combine(directory, OutputName(path, format));
                if (!File.Exists(output) || new FileInfo(output).Length <= 0)
                {
                    continue;
                }

                // Housekeeping prunes by this clock: a hit keeps the entry young.
                TryTouch(directory);
                return new ConvertedOfficeDocument(output, format == "html" ? SheetHtml.TryReadSheetList(output) : null);
            }
            catch
            {
                // An unreadable source has no cache entry.
            }
        }

        return null;
    }

    /// <summary>
    /// The job for this file: the one already running when there is one,
    /// otherwise a new one. The caller waits until its token is cancelled;
    /// once no caller waits, the job stops. A <paramref name="background"/>
    /// caller (the neighbor warm-up) never makes the work urgent: it runs
    /// below normal and gives the engine to any preview that asks.
    /// </summary>
    public static OfficeConversionJob Convert(string path, OfficeKind kind, CancellationToken waitToken, bool background = false)
    {
        var key = GetCacheDirectory(path, Formats(kind)[0]);
        OfficeConversionJob job;
        bool started;
        lock (Lane)
        {
            started = !Jobs.TryGetValue(key, out var running);
            job = running ?? new OfficeConversionJob(key, path, kind);
            if (started)
            {
                Jobs[key] = job;
            }

            job.Waiters++;
            if (!background)
            {
                job.ForegroundWaiters++;
                job.Finishing |= job.RestStarted;
            }
        }

        var who = background ? " (neighbor)" : string.Empty;
        DiagnosticLog.Write(started ? $"office job start{who} {path}" : $"office job attach{who} {path}");
        var registration = waitToken.Register(() => Detach(job, background));
        lock (Lane)
        {
            if (job.Finished)
            {
                registration.Dispose();
            }
            else
            {
                job.Registrations.Add(registration);
            }
        }

        if (started)
        {
            _ = Task.Run(() => RunJobAsync(job));
        }
        else
        {
            Rebalance();
        }

        return job;
    }

    public static OfficeProgressiveConversionPlan? TryCreateProgressivePlan(string path, OfficeKind kind)
    {
        var size = SafeFileLength(path);
        if (kind == OfficeKind.Presentation)
        {
            // A big deck with no count still converts its first slides
            // first; the rest arrive without a promised number.
            var slides = TryReadPageCount(path, "Slides");
            if (slides > ProgressivePresentationSlideThreshold
                || (slides is null && size > ProgressiveDocumentSizeThreshold))
            {
                return new OfficeProgressiveConversionPlan(
                    ProgressiveFirstPages,
                    slides ?? 0,
                    BuildPageRangeFilter("impress_pdf_Export", ProgressiveFirstPages));
            }
        }

        if (kind == OfficeKind.Document)
        {
            var pages = TryReadPageCount(path, "Pages");
            if (pages > ProgressiveDocumentPageThreshold && size > ProgressiveDocumentSizeThreshold)
            {
                return new OfficeProgressiveConversionPlan(
                    ProgressiveFirstPages,
                    pages.Value,
                    BuildPageRangeFilter("writer_pdf_Export", ProgressiveFirstPages));
            }
        }

        return null;
    }

    private static async Task RunJobAsync(OfficeConversionJob job)
    {
        ConvertedOfficeDocument? complete = null;
        try
        {
            // A finished conversion never waits behind the engine.
            complete = TryGetCached(job.SourcePath, job.Kind);
            if (complete is not null)
            {
                return;
            }

            // What the engine could not finish today it is not asked again.
            // First pages it did finish are still the preview.
            var failedEarlier = FailedEarlier(job.Key);
            var restStage = false;
            if (TryCreateProgressivePlan(job.SourcePath, job.Kind) is { } plan)
            {
                // The first pages keep an entry of their own, apart from the
                // document's: a user who leaves stops the rest, and a
                // revisit shows these at once while the rest converts again.
                var partialDirectory = GetCacheDirectory(job.SourcePath, PartialFormat);
                string? partial = Path.Combine(partialDirectory, OutputName(job.SourcePath, "pdf"));
                if (File.Exists(partial))
                {
                    TryTouch(partialDirectory);
                }
                else if (failedEarlier)
                {
                    partial = null;
                }
                else
                {
                    var staging = Path.Combine(partialDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        var (output, dropped) = await RunStageAsync(job, firstStage: true, plan.Filter, staging);
                        if (dropped)
                        {
                            return;
                        }

                        partial = output is null ? null : Publish(job.SourcePath, "pdf", output, staging, partialDirectory)?.DocumentPath;
                    }
                    finally
                    {
                        TryDeleteDirectory(staging);
                    }
                }

                if (partial is not null)
                {
                    var pages = CountPdfPages(partial);
                    if (pages is { } count && count != plan.FirstPageCount)
                    {
                        // app.xml counts go stale: the "first pages" were the
                        // whole document, so they are the cache entry.
                        complete = PromotePdfToCache(job.SourcePath, partial);
                        TryDeleteDirectory(partialDirectory);
                        return;
                    }

                    lock (Lane)
                    {
                        job.PartialDirectory = partialDirectory;
                        job.RestStarted = true;
                        job.Finishing = job.ForegroundWaiters > 0;
                    }

                    DiagnosticLog.Write($"office progressive partial ready pages={pages ?? plan.FirstPageCount}/{plan.ExpectedPageCount} {job.SourcePath}");
                    job.PublishFirst(new ConvertedOfficeDocument(
                        partial,
                        PartialPages: pages ?? plan.FirstPageCount,
                        ExpectedPages: plan.ExpectedPageCount));
                    restStage = true;
                }
            }

            if (failedEarlier)
            {
                DiagnosticLog.Write($"office job not run, it failed earlier today {job.SourcePath}");
                return;
            }

            var engineFailed = true;
            foreach (var format in Formats(job.Kind))
            {
                var directory = GetCacheDirectory(job.SourcePath, format);
                var staging = Path.Combine(directory, ".staging-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var (output, dropped) = await RunStageAsync(job, firstStage: !restStage, format, staging);
                    if (dropped)
                    {
                        return;
                    }

                    if (output is not null)
                    {
                        complete = Publish(job.SourcePath, format, output, staging, directory);
                        if (complete is not null)
                        {
                            return;
                        }

                        // The engine did its part; the cache could not take it.
                        engineFailed = false;
                    }
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
            }

            if (engineFailed)
            {
                RememberFailure(job.Key);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office job failed {job.SourcePath}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Finish(job, complete);
        }
    }

    /// <summary>
    /// One engine run under the lane. A killed run goes back in line while
    /// anyone still waits for it (another file's preview took the engine),
    /// and is dropped otherwise.
    /// </summary>
    private static async Task<(string? Output, bool Dropped)> RunStageAsync(
        OfficeConversionJob job,
        bool firstStage,
        string convertTo,
        string outputDirectory)
    {
        while (true)
        {
            var ticket = new LaneTicket(job, firstStage);
            if (!await EnterLaneAsync(ticket))
            {
                DiagnosticLog.Write($"office job dropped before it started {job.SourcePath}");
                return (null, true);
            }

            string? output;
            try
            {
                Directory.CreateDirectory(outputDirectory);
                output = Runner(new OfficeRunRequest(job.SourcePath, convertTo, outputDirectory, ticket.Control), ticket.Kill.Token);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"office conversion exception {job.SourcePath}: {ex.GetType().Name}: {ex.Message}");
                output = null;
            }
            finally
            {
                ExitLane(ticket);
                ticket.Kill.Dispose();
            }

            if (!ticket.Preempted || output is not null)
            {
                return (output, false);
            }

            bool wanted;
            lock (Lane)
            {
                wanted = job.Waiters > 0;
                if (!wanted)
                {
                    RemoveFromJobs(job);
                }
            }

            if (!wanted)
            {
                DiagnosticLog.Write($"office job stopped {job.SourcePath}: nobody is waiting");
                return (null, true);
            }

            DiagnosticLog.Write($"office job preempted, requeued {job.SourcePath}");
        }
    }

    private static Task<bool> EnterLaneAsync(LaneTicket ticket)
    {
        lock (Lane)
        {
            if (IsUnwanted(ticket.Job))
            {
                RemoveFromJobs(ticket.Job);
                ticket.Granted.TrySetResult(false);
            }
            else if (_holder is null)
            {
                Grant(ticket);
            }
            else
            {
                Waiting.Add(ticket);
            }
        }

        Rebalance();
        return ticket.Granted.Task;
    }

    private static void ExitLane(LaneTicket ticket)
    {
        lock (Lane)
        {
            if (!ReferenceEquals(_holder, ticket))
            {
                Waiting.Remove(ticket);
                return;
            }

            // Previews first, in arrival order; then everything else.
            var next = Waiting.FirstOrDefault(IsUrgent) ?? Waiting.FirstOrDefault();
            _holder = null;
            if (next is not null)
            {
                Waiting.Remove(next);
                Grant(next);
            }
        }

        Rebalance();
    }

    private static void Grant(LaneTicket ticket)
    {
        _holder = ticket;
        ticket.Granted.TrySetResult(true);
    }

    /// <summary>Every preview of the file has left: the user moved on or closed it.</summary>
    private static bool IsUnwanted(OfficeConversionJob job) => job.Waiters == 0;

    /// <summary>
    /// Under the lane lock, in the step that stops or ends the job: a preview
    /// arriving after it starts a new job instead of attaching to one that
    /// ends without output.
    /// </summary>
    private static void RemoveFromJobs(OfficeConversionJob job)
    {
        if (Jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
        {
            Jobs.Remove(job.Key);
        }
    }

    /// <summary>
    /// The holder runs at Normal only while a preview waits for its first
    /// content, and at BelowNormal while its previews read what arrived. It
    /// is killed when nobody waits for it any more, or when a preview of
    /// another file waits for the engine. Queued work nobody waits for never
    /// starts.
    /// </summary>
    private static void Rebalance()
    {
        LaneTicket? preempt = null;
        lock (PriorityGate)
        {
            LaneTicket? holder;
            List<LaneTicket>? unwatched = null;
            lock (Lane)
            {
                foreach (var ticket in Waiting.Where(ticket => IsUnwanted(ticket.Job)).ToList())
                {
                    Waiting.Remove(ticket);
                    RemoveFromJobs(ticket.Job);
                    (unwatched ??= []).Add(ticket);
                }

                // A run nobody waits for stops, unless it is the rest of a
                // document a preview showed: that one lands, but still gives
                // the engine to any preview that asks.
                holder = _holder;
                if (holder is not null
                    && !holder.Preempted
                    && ((IsUnwanted(holder.Job) && !holder.Job.Finishing) || (!IsUrgent(holder) && Waiting.Any(IsUrgent))))
                {
                    holder.Preempted = true;
                    preempt = holder;
                }
            }

            foreach (var ticket in unwatched ?? [])
            {
                ticket.Granted.TrySetResult(false);
            }

            holder?.Control.SetBackground(!IsUrgent(holder));
        }

        try
        {
            preempt?.Kill.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the decision and the kill.
        }
    }

    private static bool IsUrgent(LaneTicket ticket) => ticket.FirstStage && ticket.Job.ForegroundWaiters > 0;

    private static void Detach(OfficeConversionJob job, bool background)
    {
        bool landing;
        lock (Lane)
        {
            if (job.Finished || job.Waiters == 0)
            {
                return;
            }

            job.Waiters--;
            if (!background)
            {
                job.ForegroundWaiters = Math.Max(0, job.ForegroundWaiters - 1);
            }

            landing = job.Waiters == 0 && job.Finishing && ReferenceEquals(_holder?.Job, job);
        }

        if (landing)
        {
            DiagnosticLog.Write($"office job finishing after its preview left {job.SourcePath}");
        }

        Rebalance();
    }

    private static void Finish(OfficeConversionJob job, ConvertedOfficeDocument? complete)
    {
        List<CancellationTokenRegistration> registrations;
        string? partialToDelete = null;
        lock (Lane)
        {
            job.Finished = true;
            job.Waiters = 0;
            RemoveFromJobs(job);

            registrations = [.. job.Registrations];
            job.Registrations.Clear();

            // First pages give way to the whole document once nobody shows
            // them. Without it they are the preview, now and on a revisit,
            // so they stay and a viewer can still take hold of them.
            job.Landed = complete is not null;
            if (job.Landed && job.PartialHolds == 0)
            {
                partialToDelete = job.PartialDirectory;
                job.PartialDirectory = null;
            }
        }

        job.PublishComplete(complete);
        foreach (var registration in registrations)
        {
            registration.Dispose();
        }

        if (partialToDelete is not null)
        {
            TryDeleteDirectory(partialToDelete);
        }

        DiagnosticLog.Write(complete is null ? $"office job ended without output {job.SourcePath}" : $"office job done {job.SourcePath}");
    }

    internal static bool TryHoldPartial(OfficeConversionJob job)
    {
        lock (Lane)
        {
            if (job.PartialDirectory is null)
            {
                return false;
            }

            job.PartialHolds++;
            return true;
        }
    }

    internal static void ReleasePartial(OfficeConversionJob job)
    {
        string? partialToDelete = null;
        lock (Lane)
        {
            job.PartialHolds = Math.Max(0, job.PartialHolds - 1);
            if (job.Landed && job.PartialHolds == 0)
            {
                partialToDelete = job.PartialDirectory;
                job.PartialDirectory = null;
            }
        }

        if (partialToDelete is not null)
        {
            TryDeleteDirectory(partialToDelete);
        }
    }

    /// <summary>
    /// Moves a finished run into its cache entry, the main output last, so a
    /// reader never finds a half-written document.
    /// </summary>
    private static ConvertedOfficeDocument? Publish(string sourcePath, string format, string output, string staging, string directory)
    {
        try
        {
            if (format == "html")
            {
                SheetHtml.Normalize(output);
            }

            var target = Path.Combine(directory, OutputName(sourcePath, format));
            foreach (var file in Directory.EnumerateFiles(staging))
            {
                if (!string.Equals(file, output, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
                }
            }

            File.Move(output, target, overwrite: true);
            DiagnosticLog.Write($"office conversion ok {target}");
            return new ConvertedOfficeDocument(target, format == "html" ? SheetHtml.TryReadSheetList(target) : null);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office publish failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static ConvertedOfficeDocument? PromotePdfToCache(string sourcePath, string convertedPath)
    {
        try
        {
            var directory = GetCacheDirectory(sourcePath, "pdf");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, OutputName(sourcePath, "pdf"));
            var temporary = target + ".tmp";
            File.Copy(convertedPath, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);
            DiagnosticLog.Write($"office progressive promoted {sourcePath}");
            return new ConvertedOfficeDocument(target);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office progressive promote failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string[] Formats(OfficeKind kind) => kind switch
    {
        // Calc's print pipeline paginates wide sheets into strips and clips
        // text that overflows into empty neighbors. Its HTML export draws
        // the sheet the way Calc shows it on screen; PDF is the fallback.
        OfficeKind.Spreadsheet => ["html", "pdf"],
        OfficeKind.None => [],
        _ => ["pdf"]
    };

    private static string OutputName(string sourcePath, string format) =>
        Path.GetFileNameWithoutExtension(sourcePath) + (format == "html" ? ".html" : ".pdf");

    /// <summary>
    /// The production runner: one soffice per conversion, network-isolated
    /// three ways (sanitized source, a profile that follows no links and
    /// has a dead proxy, the same proxy in the environment), and gone with
    /// everything it started before the run returns.
    /// </summary>
    private static string? RunSoffice(OfficeRunRequest request, CancellationToken kill)
    {
        var soffice = FindSoffice();
        if (soffice is null)
        {
            DiagnosticLog.Write("office conversion skipped: compatible soffice.exe not found");
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(kill);
        request.Control.StartClock(timeout);
        try
        {
            using var preparedSource = OfficePreviewSourceSanitizer.Prepare(request.SourcePath, request.OutputDirectory, timeout.Token);
            if (preparedSource.ExternalRelationshipsRemoved > 0)
            {
                DiagnosticLog.Write(
                    $"office external relationships removed count={preparedSource.ExternalRelationshipsRemoved} {request.SourcePath}");
            }

            var profileDirectory = Path.Combine(AppPaths.DataRoot, "LibreOfficeProfile");
            PrepareNetworkIsolatedProfile(profileDirectory);
            Directory.CreateDirectory(EngineTempDirectory(request.OutputDirectory));
            using var process = new Process
            {
                StartInfo = BuildStartInfo(soffice, preparedSource.Path, request.ConvertTo, request.OutputDirectory, profileDirectory)
            };

            DiagnosticLog.Write($"office conversion start {request.SourcePath}");
            process.Start();
            Task<string> outputTask;
            Task<string> errorTask;
            try
            {
                // Before soffice.exe starts soffice.bin, so the child is
                // born in the job and at the run's priority.
                request.Control.Attach(process);
                outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            finally
            {
                // soffice.exe is a launcher: stopped alone, it leaves
                // soffice.bin converting and holding the profile.
                request.Control.End();
            }

            var log = (outputTask.GetAwaiter().GetResult() + Environment.NewLine + errorTask.GetAwaiter().GetResult()).Trim();
            if (process.ExitCode != 0)
            {
                DiagnosticLog.Write($"office conversion failed exit={process.ExitCode}: {log}");
                return null;
            }

            var extension = request.ConvertTo.StartsWith("html", StringComparison.OrdinalIgnoreCase) ? ".html" : ".pdf";
            var output = Path.Combine(request.OutputDirectory, Path.GetFileNameWithoutExtension(preparedSource.Path) + extension);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                DiagnosticLog.Write($"office conversion produced no output: {log}");
                return null;
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write($"office conversion {(kill.IsCancellationRequested ? "stopped" : "timed out")} {request.SourcePath}");
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office conversion exception {request.SourcePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string EngineTempDirectory(string outputDirectory) => Path.Combine(outputDirectory, "engine-tmp");

    internal static ProcessStartInfo BuildStartInfo(
        string soffice,
        string sourcePath,
        string convertTo,
        string outputDirectory,
        string profileDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = soffice,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var blockedProxy = $"http://{BlockedProxyHost}:{BlockedProxyPort}";
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
        {
            startInfo.Environment[key] = blockedProxy;
        }

        startInfo.Environment["NO_PROXY"] = string.Empty;
        startInfo.Environment["no_proxy"] = string.Empty;

        // LibreOffice's own scratch (lu*.tmp) lands in the run's staging
        // folder, which goes when the run ends. In %TEMP% every stopped run
        // left one behind for good.
        startInfo.Environment["TMP"] = EngineTempDirectory(outputDirectory);
        startInfo.Environment["TEMP"] = EngineTempDirectory(outputDirectory);
        foreach (var argument in new[]
                 {
                     "--headless", "--nologo", "--nodefault", "--nofirststartwizard", "--norestore", "--nolockcheck",
                     "-env:UserInstallation=" + new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri,
                     "--convert-to", convertTo,
                     "--outdir", outputDirectory,
                     sourcePath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static void PrepareNetworkIsolatedProfile(string profileDirectory)
    {
        var userDirectory = Path.Combine(profileDirectory, "user");
        Directory.CreateDirectory(userDirectory);
        var settingsPath = Path.Combine(userDirectory, "registrymodifications.xcu");

        var document = File.Exists(settingsPath)
            ? XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace)
            : new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(OfficeRegistry + "items", new XAttribute(XNamespace.Xmlns + "oor", OfficeRegistry)));

        var root = document.Root ?? throw new InvalidDataException("LibreOffice profile has no settings root.");
        var port = BlockedProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var settings = new (string Node, (string Name, string Value)[] Values)[]
        {
            ("/org.openoffice.Inet/Settings",
            [
                ("ooInetProxyType", "2"),
                ("ooInetHTTPProxyName", BlockedProxyHost),
                ("ooInetHTTPProxyPort", port),
                ("ooInetHTTPSProxyName", BlockedProxyHost),
                ("ooInetHTTPSProxyPort", port),
                ("ooInetNoProxy", string.Empty)
            ]),

            // The proxy stops http. A picture linked by file path or from a
            // share (\\host\share, which starts a Windows sign-in to that
            // host) is stopped by this switch: no profile location is
            // trusted, so every document's links are blocked.
            ("/org.openoffice.Office.Common/Security/Scripting",
            [
                ("BlockUntrustedRefererLinks", "true")
            ])
        };

        foreach (var (node, values) in settings)
        {
            var protectedNames = values.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var item in root.Elements("item")
                         .Where(item => (string?)item.Attribute(OfficeRegistry + "path") == node)
                         .ToList())
            {
                foreach (var property in item.Elements("prop")
                             .Where(property => protectedNames.Contains((string?)property.Attribute(OfficeRegistry + "name") ?? string.Empty))
                             .ToList())
                {
                    property.Remove();
                }

                if (!item.Elements().Any())
                {
                    item.Remove();
                }
            }

            var ours = new XElement("item", new XAttribute(OfficeRegistry + "path", node));
            foreach (var (name, value) in values)
            {
                ours.Add(new XElement(
                    "prop",
                    new XAttribute(OfficeRegistry + "name", name),
                    new XAttribute(OfficeRegistry + "op", "fuse"),
                    new XElement("value", value)));
            }

            root.Add(ours);
        }

        var tempPath = settingsPath + ".tmp";
        document.Save(tempPath);
        File.Move(tempPath, settingsPath, overwrite: true);
    }

    /// <summary>
    /// Found once and kept while the file is there; a missing engine is
    /// looked for again, so installing LibreOffice works without a restart.
    /// </summary>
    private static string? FindSoffice()
    {
        var overridePath = Environment.GetEnvironmentVariable("SPACEPREVIEW_SOFFICE");
        if (string.Equals(overridePath, "off", StringComparison.OrdinalIgnoreCase))
        {
            // Shows the engine-less preview on a machine that has the engine.
            return null;
        }

        var known = Volatile.Read(ref _soffice);
        if (known is not null && File.Exists(known))
        {
            return known;
        }

        var found = GetDefaultSofficeCandidates(overridePath, AppPaths.LocalAppData)
            .Concat(GetRegistrySofficeCandidates())
            .Concat(GetPathSofficeCandidates())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
        Volatile.Write(ref _soffice, found);
        return found;
    }

    internal static IReadOnlyList<string> GetDefaultSofficeCandidates(string? overridePath, string localAppData) =>
    [
        overridePath ?? string.Empty,
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        Path.Combine(localAppData, @"Programs\LibreOffice\program\soffice.exe"),
        @"C:\Program Files\OpenOffice 4\program\soffice.exe",
        @"C:\Program Files (x86)\OpenOffice 4\program\soffice.exe",
        Path.Combine(localAppData, @"Programs\OpenOffice 4\program\soffice.exe")
    ];

    private static IEnumerable<string> GetRegistrySofficeCandidates()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var subkey in new[]
                     {
                         @"SOFTWARE\LibreOffice\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath",
                         @"SOFTWARE\OpenOffice\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\OpenOffice\UNO\InstallPath",
                         @"SOFTWARE\OpenOffice.org\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\OpenOffice.org\UNO\InstallPath"
                     })
            {
                using var key = root.OpenSubKey(subkey);
                if (key?.GetValue(null) is string installPath && !string.IsNullOrWhiteSpace(installPath))
                {
                    yield return Path.Combine(installPath, "soffice.exe");
                }
            }
        }
    }

    private static IEnumerable<string> GetPathSofficeCandidates()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim(), "soffice.exe");
            }
            catch
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static string BuildPageRangeFilter(string filter, int firstPages) =>
        $"pdf:{filter}:{{\"PageRange\":{{\"type\":\"string\",\"value\":\"1-{firstPages}\"}}}}";

    /// <summary>
    /// The page or slide count the authoring app saved: OOXML's app.xml
    /// element, or ODF's meta:page-count (Writer keeps one; Impress does not).
    /// </summary>
    private static int? TryReadPageCount(string path, string ooxmlElement)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var app = archive.GetEntry("docProps/app.xml");
            var meta = archive.GetEntry("meta.xml");
            var entry = app ?? meta;
            if (entry is null || entry.Length <= 0 || entry.Length > MaxAppPropertiesBytes)
            {
                return null;
            }

            var document = XmlNesting.Load(
                entry.Open,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null },
                LoadOptions.None);
            var text = app is not null
                ? document.Descendants().FirstOrDefault(element => element.Name.LocalName.Equals(ooxmlElement, StringComparison.OrdinalIgnoreCase))?.Value
                : document.Descendants().FirstOrDefault(element => element.Name.LocalName == "document-statistic")
                    ?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "page-count")?.Value;
            return int.TryParse(text, out var value) && value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pages in a PDF LibreOffice wrote; null when its objects are compressed out of reach.</summary>
    private static int? CountPdfPages(string path)
    {
        try
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            var count = Regex.Matches(text, @"/Type\s*/Page\b", RegexOptions.CultureInvariant).Count;
            return count > 0 ? count : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetCacheDirectory(string path, string format)
    {
        var file = new FileInfo(path);
        var input = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{format}|{PipelineVersion}";
        var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
        return Path.Combine(AppPaths.DataRoot, "DocumentCache", hash);
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool FailedEarlier(string cacheDirectory)
    {
        try
        {
            var mark = new FileInfo(Path.Combine(cacheDirectory, FailureMark));
            return mark.Exists && DateTime.UtcNow - mark.LastWriteTimeUtc < FailureMemory;
        }
        catch
        {
            return false;
        }
    }

    private static void RememberFailure(string cacheDirectory)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            File.WriteAllBytes(Path.Combine(cacheDirectory, FailureMark), []);
        }
        catch
        {
            // Not remembered: the next look asks the engine again.
        }
    }

    private static void TryTouch(string directory)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow);
        }
        catch
        {
            // The clock is best effort.
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Housekeeping removes what a live viewer still held.
        }
    }

    private sealed class LaneTicket(OfficeConversionJob job, bool firstStage)
    {
        public OfficeConversionJob Job { get; } = job;
        public bool FirstStage { get; } = firstStage;
        public TaskCompletionSource<bool> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Kill { get; } = new();
        public OfficeRunControl Control { get; } = new();
        public bool Preempted { get; set; }
    }
}
