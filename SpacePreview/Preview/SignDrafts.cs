using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>A file's version as a draft pins it: its length and last write, to the tick.</summary>
internal readonly record struct FileStamp(long Length, long LastWriteUtcTicks)
{
    public static FileStamp? Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Work brought back from a draft, made on exactly the version of the file
/// now on disk. Lost counts the placements that could not come back: their
/// signature or stamp was replaced or removed since.
/// </summary>
internal sealed record SignDraft(int PageIndex, IReadOnlyList<SignPlacement> Placements, int Lost = 0);

/// <summary>
/// Unsaved sign-mode work, kept in the data root so an upgrade, Windows
/// ending the session or a crash never costs the user what they placed.
/// One small file per PDF, named by a hash of its path, holding positions,
/// pages, date text and references to the user's own signature and stamp
/// files. Ink redrawn on the pad has no file of its own and travels inline.
/// The PDF itself is written only by Save.
/// </summary>
internal static class SignDrafts
{
    public static readonly TimeSpan Abandoned = TimeSpan.FromDays(30);

    // Reads, writes and deletes of draft files, from any thread, one at a time.
    private static readonly object Io = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The PDF as drafts and sign sessions know it: one name per file, in any case.</summary>
    public static string Canonical(string pdfPath) => Path.GetFullPath(pdfPath).ToUpperInvariant();

    public static string PathFor(string pdfPath)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(pdfPath)));
        return AppPaths.InDataRoot("SignDrafts", Convert.ToHexString(key, 0, 16) + ".json");
    }

    /// <summary>
    /// The file's version now, and the draft made on it if there is one. A
    /// draft for another version, one untouched for 30 days, or one whose
    /// ink is all gone is deleted here, the moment it is next looked at.
    /// </summary>
    public static (FileStamp? File, SignDraft? Draft) Load(string pdfPath)
    {
        var file = FileStamp.Of(pdfPath);
        lock (Io)
        {
            var draftPath = PathFor(pdfPath);
            byte[] json;
            try
            {
                var info = new FileInfo(draftPath);
                if (!info.Exists)
                {
                    return (file, null);
                }

                if (DateTime.UtcNow - info.LastWriteTimeUtc > Abandoned)
                {
                    Drop(draftPath, "untouched for 30 days");
                    return (file, null);
                }

                json = File.ReadAllBytes(draftPath);
            }
            catch (Exception ex)
            {
                // Locked or unreadable for now: kept for next time.
                DiagnosticLog.Write($"fill-sign draft unreadable: {ex.GetType().Name}: {ex.Message}");
                return (file, null);
            }

            DraftFile? stored;
            try
            {
                stored = JsonSerializer.Deserialize<DraftFile>(json, Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                Drop(draftPath, $"corrupt, {ex.GetType().Name}");
                return (file, null);
            }

            if (stored?.Placements is null)
            {
                Drop(draftPath, "corrupt, no placements");
                return (file, null);
            }

            // Never applied to any other version of the file: positions
            // on a changed document point at whatever is there now.
            if (!string.Equals(stored.Path, Path.GetFullPath(pdfPath), StringComparison.OrdinalIgnoreCase)
                || file != new FileStamp(stored.Length, stored.LastWriteUtcTicks))
            {
                Drop(draftPath, "the PDF changed since");
                return (file, null);
            }

            var kept = stored.Placements
                .Select(item => (Item: item, Placement: Revive(item)))
                .Where(pair => pair.Placement is not null)
                .ToArray();
            if (kept.Length == 0)
            {
                Drop(draftPath, "its signatures and stamps are gone");
                return (file, null);
            }

            // The count stays in the draft until the session that resumes it
            // has said it: this look may be the Sign pill deciding its label,
            // long before anyone reads the notice.
            var lost = Math.Max(0, stored.Lost) + stored.Placements.Length - kept.Length;
            if (kept.Length < stored.Placements.Length)
            {
                DiagnosticLog.Write($"fill-sign draft let go of {stored.Placements.Length - kept.Length} placements whose ink is gone");
                Save(draftPath, stored with { Placements = kept.Select(pair => pair.Item).ToArray(), Lost = lost });
            }

            return (file, new SignDraft(Math.Max(0, stored.Page), kept.Select(pair => pair.Placement!).ToArray(), lost));
        }
    }

    /// <summary>
    /// Replaces the PDF's draft with this work; no work deletes it. Runs on
    /// a timer thread, where anything thrown would end the app: a draft
    /// that cannot be written is a log line.
    /// </summary>
    public static void Write(string pdfPath, FileStamp file, int pageIndex, IReadOnlyList<SignPlacement> placements)
    {
        lock (Io)
        {
            try
            {
                var items = placements.Select(ToDraft).OfType<DraftPlacement>().ToArray();
                if (items.Length == 0)
                {
                    Delete(pdfPath);
                    return;
                }

                Save(PathFor(pdfPath), new DraftFile(Path.GetFullPath(pdfPath), file.Length, file.LastWriteUtcTicks, pageIndex, items));
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"fill-sign draft write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public static void Delete(string pdfPath)
    {
        lock (Io)
        {
            try
            {
                File.Delete(PathFor(pdfPath));
            }
            catch (DirectoryNotFoundException)
            {
                // No drafts at all.
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"fill-sign draft delete failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Beside the draft, then over it: a forced quit mid-write leaves the previous draft whole.</summary>
    private static void Save(string draftPath, DraftFile draft)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(draftPath)!);
            var temp = draftPath + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(draft, Options));
            File.Move(temp, draftPath, overwrite: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign draft write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Drop(string draftPath, string reason)
    {
        try
        {
            File.Delete(draftPath);
        }
        catch
        {
            // Left in place, it fails the same checks next time.
        }

        DiagnosticLog.Write($"fill-sign draft dropped: {reason}");
    }

    private static DraftPlacement? ToDraft(SignPlacement placement)
    {
        if (placement.Kind == SignPlacementKind.Date)
        {
            return new DraftPlacement(placement.Kind, placement.PageIndex, placement.X, placement.Y, placement.Width, placement.Height, placement.Text, null, null, null);
        }

        // Only the user's own files are referenced, by their place in the data root.
        if (placement.Source is not { } source)
        {
            return null;
        }

        var relative = Path.GetRelativePath(AppPaths.DataRoot, source.File);
        if (InDataRoot(relative) is null)
        {
            return null;
        }

        return new DraftPlacement(
            placement.Kind,
            placement.PageIndex,
            placement.X,
            placement.Y,
            placement.Width,
            placement.Height,
            null,
            relative,
            source.Fingerprint,
            source.Redrawn ? placement.ImagePng : null);
    }

    /// <summary>The placement again, or null when its ink no longer exists as it was placed.</summary>
    private static SignPlacement? Revive(DraftPlacement item)
    {
        if (item is null || item.Page < 0)
        {
            return null;
        }

        if (item.Kind == SignPlacementKind.Date)
        {
            var text = SignText.Writable(item.Text ?? string.Empty);
            return text.Length == 0 ? null : new SignPlacement(item.Kind, item.Page, item.X, item.Y, item.Width, item.Height, text).Clamped();
        }

        // A stamp replaced since is a different image under the same name.
        if (item.Kind is not (SignPlacementKind.Signature or SignPlacementKind.Stamp)
            || InDataRoot(item.Image) is not { } file
            || SignatureStore.TryReadForPlacement(file, out var source) is not { } png
            || source is null
            || source.Fingerprint != item.Fingerprint
            || item.Ink is { } ink && SignatureStore.TryLoad(ink) is null)
        {
            return null;
        }

        return new SignPlacement(
            item.Kind,
            item.Page,
            item.X,
            item.Y,
            item.Width,
            item.Height,
            null,
            item.Ink ?? png,
            source with { Redrawn = item.Ink is not null }).Clamped();
    }

    /// <summary>The file a draft names, if it lies inside the data root; anything else is no file.</summary>
    private static string? InDataRoot(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.DataRoot)) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Lost is how many placements earlier looks had to let go of, not yet told to the user.</summary>
    private sealed record DraftFile(string Path, long Length, long LastWriteUtcTicks, int Page, DraftPlacement[] Placements, int Lost = 0);

    /// <summary>Image is relative to the data root; Ink only for ink redrawn on the pad.</summary>
    private sealed record DraftPlacement(
        SignPlacementKind Kind,
        int Page,
        double X,
        double Y,
        double Width,
        double Height,
        string? Text,
        string? Image,
        string? Fingerprint,
        byte[]? Ink);
}

/// <summary>
/// One sign session's draft. A change replaces the session's work and
/// restarts a short pause; the write happens on the thread pool once the
/// user stops, and at once when the session or the app ends. The keeper
/// holds on to its work after writing it, so the app's end writes every
/// live session's work again, whatever another session did to the draft
/// meanwhile. A session only deletes a draft it restored or wrote, so a
/// second window that placed nothing cannot erase the first one's work.
/// </summary>
internal sealed class SignDraftKeeper
{
    public static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    // Guards every keeper's fields and the set of live sessions; never held over disk work.
    private static readonly object State = new();
    private static readonly HashSet<SignDraftKeeper> Live = [];

    private readonly string _pdfPath;
    private readonly FileStamp _file;
    private readonly Timer _timer;
    private readonly object _writing = new();
    private (int Page, SignPlacement[] Placements) _work;
    private bool _changed;
    private bool _owns;

    /// <summary>Starts from the work already on the surface: what a draft brought back, or none.</summary>
    public SignDraftKeeper(string pdfPath, FileStamp file, int pageIndex, IReadOnlyList<SignPlacement> work)
    {
        _pdfPath = pdfPath;
        _file = file;
        _work = (pageIndex, work.ToArray());
        _owns = work.Count > 0;
        _timer = new Timer(_ => Write(onlyChanged: true));
        lock (State)
        {
            Live.Add(this);
        }
    }

    /// <summary>The version of the PDF this session's work was placed on.</summary>
    public FileStamp File => _file;

    /// <summary>Takes the work as it is now and restarts the pause. Never touches the disk.</summary>
    public void Update(int pageIndex, IReadOnlyList<SignPlacement> placements)
    {
        lock (State)
        {
            if (!Live.Contains(this) || placements.Count == 0 && !_owns)
            {
                return;
            }

            _owns = true;
            _work = (pageIndex, placements.ToArray());
            _changed = true;
            _timer.Change(Pause, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The session ends with its work unsaved: the work is written now, and
    /// nothing after. True when there was work to keep.
    /// </summary>
    public bool Close() => End(keep: true);

    /// <summary>The work was saved or thrown away: the draft goes, and nothing more is written.</summary>
    public void Discard() => End(keep: false);

    /// <summary>
    /// Every live session's work to disk, synchronously, each after any
    /// write of its own already under way: the app is ending. True when
    /// there was work to keep.
    /// </summary>
    public static bool FlushAll()
    {
        SignDraftKeeper[] live;
        lock (State)
        {
            live = [.. Live];
        }

        var kept = false;
        foreach (var keeper in live)
        {
            kept |= keeper.Write(onlyChanged: false);
        }

        return kept;
    }

    /// <summary>The work to disk, or only a change still waiting out its pause. True when it wrote work.</summary>
    private bool Write(bool onlyChanged)
    {
        lock (_writing)
        {
            (int Page, SignPlacement[] Placements) work;
            lock (State)
            {
                if (!Live.Contains(this) || onlyChanged && !_changed || _work.Placements.Length == 0 && !_owns)
                {
                    return false;
                }

                work = _work;
                _changed = false;
                _owns = work.Placements.Length > 0;
            }

            SignDrafts.Write(_pdfPath, _file, work.Page, work.Placements);
            return work.Placements.Length > 0;
        }
    }

    private bool End(bool keep)
    {
        lock (_writing)
        {
            var kept = keep && Write(onlyChanged: false);

            // A session closed first and saved after still takes its draft
            // with it, once.
            bool delete;
            lock (State)
            {
                Live.Remove(this);
                delete = !keep && _owns;
                if (delete)
                {
                    _owns = false;
                }
            }

            _timer.Dispose();
            if (delete)
            {
                SignDrafts.Delete(_pdfPath);
            }

            return kept;
        }
    }
}
