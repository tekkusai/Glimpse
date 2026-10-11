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
/// The running engine's priority. Work no preview is blocked on (the rest
/// of a document whose first pages show) drops to BelowNormal; soffice.exe
/// is only a launcher, so the change has to reach its soffice.bin child too.
/// </summary>
internal sealed class OfficeRunControl
{
    private readonly object _gate = new();
    private Process? _process;

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
            Apply();
        }
    }

    public void Attach(Process process)
    {
        lock (_gate)
        {
            _process = process;
            if (Background)
            {
                Apply();
            }
        }
    }

    private void Apply()
    {
        if (_process is null)
        {
            return;
        }

        var priority = Background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
        try
        {
            _process.PriorityClass = priority;
            foreach (var childId in ProcessTree.ChildrenOf(_process.Id))
            {
                using var child = Process.GetProcessById(childId);
                child.PriorityClass = priority;
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

        public static List<int> ChildrenOf(int parentId)
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
/// file. It runs while one of them still waits for it. When the last one
/// leaves, the engine stops; finished first pages stay cached, so a revisit
/// shows them at once and converts only the rest.
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
    internal bool Finished;
    internal string? PartialDirectory;
    internal int PartialHolds;
    internal readonly List<CancellationTokenRegistration> Registrations = [];

    /// <summary>
    /// A viewer showing the partial keeps its folder alive until it lets go.
    /// False when the folder is already gone; show Complete instead.
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
    private const int ConversionTimeoutSeconds = 75;
    private const int ProgressiveFirstPages = 6;
    private const int ProgressivePresentationSlideThreshold = 10;
    private const int ProgressiveDocumentPageThreshold = 25;
    private const long ProgressiveDocumentSizeThreshold = 1_500_000;
    private const long MaxAppPropertiesBytes = 256 * 1024;
    private const string BlockedProxyHost = "127.0.0.1";
    private const int BlockedProxyPort = 9;

    // The pipeline version is the cache's invalidation: bump it whenever
    // conversion or sheet styling changes, or old output keeps being served.
    private const string PipelineVersion = "v14";

    // The cache key of a big document's first pages, beside its whole-document formats.
    private const string PartialFormat = "first-pages";

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

    public static string? EnginePath => FindSoffice();

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
    /// once no caller waits, the job stops.
    /// </summary>
    public static OfficeConversionJob Convert(string path, OfficeKind kind, CancellationToken waitToken)
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
        }

        DiagnosticLog.Write(started ? $"office job start {path}" : $"office job attach {path}");
        var registration = waitToken.Register(() => Detach(job));
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

    /// <summary>Blocking form for callers that only want the finished document.</summary>
    public static ConvertedOfficeDocument? TryConvert(string path, CancellationToken cancellationToken)
    {
        var kind = FileKinds.GetOfficeKind(path);
        if (kind == OfficeKind.None)
        {
            return null;
        }

        if (TryGetCached(path, kind) is { } cached)
        {
            return cached;
        }

        try
        {
            var job = Convert(path, kind, cancellationToken);
            return job.Complete.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
                    }

                    DiagnosticLog.Write($"office progressive partial ready {job.SourcePath} pages={pages ?? plan.FirstPageCount}/{plan.ExpectedPageCount}");
                    job.PublishFirst(new ConvertedOfficeDocument(
                        partial,
                        PartialPages: pages ?? plan.FirstPageCount,
                        ExpectedPages: plan.ExpectedPageCount));
                    restStage = true;
                }
            }

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
                    }
                }
                finally
                {
                    TryDeleteDirectory(staging);
                }
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

            DiagnosticLog.Write($"office job preempted {job.SourcePath} requeued");
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

                holder = _holder;
                if (holder is not null
                    && !holder.Preempted
                    && (IsUnwanted(holder.Job) || (!IsUrgent(holder) && Waiting.Any(IsUrgent))))
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

    private static bool IsUrgent(LaneTicket ticket) => ticket.FirstStage && ticket.Job.Waiters > 0;

    private static void Detach(OfficeConversionJob job)
    {
        lock (Lane)
        {
            if (job.Finished || job.Waiters == 0)
            {
                return;
            }

            job.Waiters--;
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
            // them. Without it they are what a revisit shows, so they stay.
            if (complete is null)
            {
                job.PartialDirectory = null;
            }
            else if (job.PartialHolds == 0)
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
            if (job.Finished && job.PartialHolds == 0)
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
    /// three ways (sanitized source, blocked-proxy profile, blocked-proxy
    /// environment), killed with its whole tree when cancelled.
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
        timeout.CancelAfter(TimeSpan.FromSeconds(ConversionTimeoutSeconds));
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
            using var process = new Process
            {
                StartInfo = BuildStartInfo(soffice, preparedSource.Path, request.ConvertTo, request.OutputDirectory, profileDirectory)
            };

            DiagnosticLog.Write($"office conversion start {request.SourcePath}");
            process.Start();
            // Set before soffice.exe spawns soffice.bin, so the child inherits it.
            request.Control.Attach(process);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // soffice.exe is a launcher: disposing it leaves soffice.bin
                // converting, and holding the profile, long after we left.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It may have exited between the cancel and the kill.
                }

                throw;
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
        const string settingsNode = "/org.openoffice.Inet/Settings";
        var port = BlockedProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var values = new (string Name, string Value)[]
        {
            ("ooInetProxyType", "2"),
            ("ooInetHTTPProxyName", BlockedProxyHost),
            ("ooInetHTTPProxyPort", port),
            ("ooInetHTTPSProxyName", BlockedProxyHost),
            ("ooInetHTTPSProxyPort", port),
            ("ooInetNoProxy", string.Empty)
        };

        var protectedNames = values.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in root.Elements("item")
                     .Where(item => (string?)item.Attribute(OfficeRegistry + "path") == settingsNode)
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

        var networkItem = new XElement("item", new XAttribute(OfficeRegistry + "path", settingsNode));
        foreach (var (name, value) in values)
        {
            networkItem.Add(new XElement(
                "prop",
                new XAttribute(OfficeRegistry + "name", name),
                new XAttribute(OfficeRegistry + "op", "fuse"),
                new XElement("value", value)));
        }

        root.Add(networkItem);
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
