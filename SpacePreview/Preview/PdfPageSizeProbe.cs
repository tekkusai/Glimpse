using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SpacePreview.Preview;

internal static class PdfPageSizeProbe
{
    private const int PrefixBytes = 4 * 1024 * 1024;
    private const int SuffixBytes = 512 * 1024;
    private const int MaxObjects = 4096;
    private const int MaxObjectBodyBytes = 512 * 1024;
    private const int MaxTreeDepth = 32;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromMilliseconds(500);

    private static readonly Regex ObjectRegex = new(
        @"(?ms)(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+obj\b(?<body>.*?)\bendobj\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex NumberRegex = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    private static readonly Regex TypeRegex = new(
        @"/Type\s*/(?<type>Catalog|Pages|Page)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        MatchTimeout);

    public static bool TryReadFirstPageSize(string path, out double widthPoints, out double heightPoints)
    {
        widthPoints = 0;
        heightPoints = 0;

        try
        {
            var budget = new ProbeBudget();
            if (!TryReadProbeText(path, out var text))
            {
                return false;
            }

            if (text.Contains("/Encrypt", StringComparison.Ordinal))
            {
                return false;
            }

            budget.ThrowIfExpired();
            var objects = PdfObjectIndex.Parse(text, budget);
            if (objects.Count == 0)
            {
                return false;
            }

            if (TryFindRoot(objects, text, out var root)
                && TryReadReference(root.Body, "Pages", out var pages)
                && TryReadFirstPageFromTree(objects, pages, new PageBoxes(null, null), null, 0, [], budget, out widthPoints, out heightPoints))
            {
                return true;
            }

            return TryReadFirstPageByObjectOrder(objects, budget, out widthPoints, out heightPoints);
        }
        catch
        {
            widthPoints = 0;
            heightPoints = 0;
            return false;
        }
    }

    private static bool TryReadProbeText(string path, out string text)
    {
        text = string.Empty;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 8)
        {
            return false;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (info.Length <= PrefixBytes + SuffixBytes)
        {
            var bytes = new byte[(int)info.Length];
            ReadExactlyEnough(stream, bytes);
            text = Encoding.Latin1.GetString(bytes);
            return text.Contains("%PDF-", StringComparison.Ordinal);
        }

        var prefix = new byte[PrefixBytes];
        ReadExactlyEnough(stream, prefix);

        var suffix = new byte[SuffixBytes];
        stream.Seek(-SuffixBytes, SeekOrigin.End);
        ReadExactlyEnough(stream, suffix);

        text = Encoding.Latin1.GetString(prefix) + "\n" + Encoding.Latin1.GetString(suffix);
        return text.Contains("%PDF-", StringComparison.Ordinal);
    }

    private static void ReadExactlyEnough(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }
    }

    private static bool TryFindRoot(PdfObjectIndex objects, string text, out PdfObject root)
    {
        root = default;
        if (TryReadReference(text, "Root", out var rootRef)
            && objects.TryGet(rootRef, out root))
        {
            return true;
        }

        foreach (var obj in objects.InFileOrder)
        {
            if (TryReadType(obj.Body, out var type) && type == "Catalog")
            {
                root = obj;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFirstPageFromTree(
        PdfObjectIndex objects,
        PdfRef reference,
        PageBoxes inheritedBoxes,
        int? inheritedRotate,
        int depth,
        HashSet<PdfRef> seen,
        ProbeBudget budget,
        out double widthPoints,
        out double heightPoints)
    {
        widthPoints = 0;
        heightPoints = 0;
        budget.ThrowIfExpired();
        if (depth > MaxTreeDepth || !seen.Add(reference) || !objects.TryGet(reference, out var obj))
        {
            return false;
        }

        if (!TryReadType(obj.Body, out var type))
        {
            return false;
        }

        var pageBoxes = new PageBoxes(
            TryReadBox(objects, obj.Body, "MediaBox", out var ownMediaBox) ? ownMediaBox : inheritedBoxes.MediaBox,
            TryReadBox(objects, obj.Body, "CropBox", out var ownCropBox) ? ownCropBox : inheritedBoxes.CropBox);
        var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : inheritedRotate;

        if (type == "Page")
        {
            return pageBoxes.DisplayBox is { } box
                && TryApplyRotation(box, rotate ?? 0, out widthPoints, out heightPoints);
        }

        if (type != "Pages" || !TryReadKids(obj.Body, out var kids))
        {
            return false;
        }

        foreach (var kid in kids)
        {
            budget.ThrowIfExpired();
            if (TryReadFirstPageFromTree(objects, kid, pageBoxes, rotate, depth + 1, seen, budget, out widthPoints, out heightPoints))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadFirstPageByObjectOrder(
        PdfObjectIndex objects,
        ProbeBudget budget,
        out double widthPoints,
        out double heightPoints)
    {
        widthPoints = 0;
        heightPoints = 0;
        foreach (var obj in objects.InFileOrder)
        {
            budget.ThrowIfExpired();
            if (!TryReadType(obj.Body, out var type) || type != "Page")
            {
                continue;
            }

            var mediaBox = TryReadBox(objects, obj.Body, "MediaBox", out var ownMediaBox) ? ownMediaBox : (PageBox?)null;
            var cropBox = TryReadBox(objects, obj.Body, "CropBox", out var ownCropBox) ? ownCropBox : (PageBox?)null;
            var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : (int?)null;
            var parent = TryReadReference(obj.Body, "Parent", out var parentRef) ? parentRef : (PdfRef?)null;
            var seen = new HashSet<PdfRef>();
            var depth = 0;
            while (parent is { } current && depth++ <= MaxTreeDepth && seen.Add(current) && objects.TryGet(current, out var parentObject))
            {
                budget.ThrowIfExpired();
                if (mediaBox is null && TryReadBox(objects, parentObject.Body, "MediaBox", out var inheritedMediaBox))
                {
                    mediaBox = inheritedMediaBox;
                }

                if (cropBox is null && TryReadBox(objects, parentObject.Body, "CropBox", out var inheritedCropBox))
                {
                    cropBox = inheritedCropBox;
                }

                if (rotate is null && TryReadRotate(objects, parentObject.Body, out var inheritedRotate))
                {
                    rotate = inheritedRotate;
                }

                parent = TryReadReference(parentObject.Body, "Parent", out var nextParent) ? nextParent : null;
            }

            var finalBox = cropBox ?? mediaBox;
            if (finalBox is { } box && TryApplyRotation(box, rotate ?? 0, out widthPoints, out heightPoints))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadType(string body, out string type)
    {
        type = string.Empty;
        var match = TypeRegex.Match(Header(body));
        if (!match.Success)
        {
            return false;
        }

        type = match.Groups["type"].Value;
        return true;
    }

    private static bool TryReadBox(PdfObjectIndex objects, string body, string name, out PageBox box)
    {
        box = default;
        var header = Header(body);
        var direct = Regex.Match(
            header,
            $@"/{Regex.Escape(name)}\s*\[(?<values>[^\]]{{1,512}})\]",
            RegexOptions.CultureInvariant,
            MatchTimeout);
        if (direct.Success)
        {
            return TryParseBox(direct.Groups["values"].Value, out box);
        }

        if (TryReadReference(header, name, out var boxRef)
            && objects.TryGet(boxRef, out var boxObject))
        {
            var bracket = Regex.Match(
                Header(boxObject.Body),
                @"\[(?<values>[^\]]{1,512})\]",
                RegexOptions.CultureInvariant,
                MatchTimeout);
            return bracket.Success && TryParseBox(bracket.Groups["values"].Value, out box);
        }

        return false;
    }

    private static bool TryReadRotate(PdfObjectIndex objects, string body, out int rotate)
    {
        rotate = 0;
        var header = Header(body);
        var direct = Regex.Match(
            header,
            @"/Rotate\s+(?<value>[-+]?\d{1,5})(?![.\d])",
            RegexOptions.CultureInvariant,
            MatchTimeout);
        if (direct.Success
            && int.TryParse(direct.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate))
        {
            return true;
        }

        if (TryReadReference(header, "Rotate", out var rotateRef)
            && objects.TryGet(rotateRef, out var rotateObject))
        {
            var value = Regex.Match(
                Header(rotateObject.Body),
                @"[-+]?\d{1,5}",
                RegexOptions.CultureInvariant,
                MatchTimeout);
            return value.Success
                && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate);
        }

        return false;
    }

    private static bool TryReadKids(string body, out IReadOnlyList<PdfRef> kids)
    {
        kids = [];
        var match = Regex.Match(
            Header(body),
            @"/Kids\s*\[(?<kids>[^\]]{1,8192})\]",
            RegexOptions.CultureInvariant,
            MatchTimeout);
        if (!match.Success)
        {
            return false;
        }

        var refs = ReadReferences(match.Groups["kids"].Value);
        if (refs.Count == 0)
        {
            return false;
        }

        kids = refs;
        return true;
    }

    private static bool TryReadReference(string body, string name, out PdfRef reference)
    {
        reference = default;
        var match = Regex.Match(
            body,
            $@"/{Regex.Escape(name)}\s+(?<number>\d{{1,10}})\s+(?<generation>\d{{1,5}})\s+R(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant,
            MatchTimeout);
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            return false;
        }

        reference = new PdfRef(number, generation);
        return true;
    }

    private static List<PdfRef> ReadReferences(string text)
    {
        var matches = Regex.Matches(
            text,
            @"(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+R(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant,
            MatchTimeout);
        var refs = new List<PdfRef>(matches.Count);
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            {
                refs.Add(new PdfRef(number, generation));
            }
        }

        return refs;
    }

    private static bool TryParseBox(string text, out PageBox box)
    {
        box = default;
        var numbers = NumberRegex.Matches(text);
        if (numbers.Count < 4)
        {
            return false;
        }

        Span<double> values = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(numbers[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                || !double.IsFinite(values[i]))
            {
                return false;
            }
        }

        box = new PageBox(values[0], values[1], values[2], values[3]);
        return box.IsSane;
    }

    private static bool TryApplyRotation(PageBox box, int rotate, out double widthPoints, out double heightPoints)
    {
        widthPoints = box.Width;
        heightPoints = box.Height;
        var normalized = ((rotate % 360) + 360) % 360;
        if (normalized is 90 or 270)
        {
            (widthPoints, heightPoints) = (heightPoints, widthPoints);
        }

        return widthPoints > 0 && heightPoints > 0;
    }

    private static string Header(string body)
    {
        var streamIndex = body.IndexOf("stream", StringComparison.Ordinal);
        return streamIndex >= 0 ? body[..streamIndex] : body;
    }

    private readonly record struct PdfRef(int Number, int Generation);

    private readonly record struct PdfObject(PdfRef Ref, string Body, int Position);

    private readonly record struct PageBoxes(PageBox? MediaBox, PageBox? CropBox)
    {
        public PageBox? DisplayBox => CropBox ?? MediaBox;
    }

    private readonly record struct PageBox(double X1, double Y1, double X2, double Y2)
    {
        public double Width => Math.Abs(X2 - X1);
        public double Height => Math.Abs(Y2 - Y1);

        public bool IsSane =>
            double.IsFinite(Width)
            && double.IsFinite(Height)
            && Width >= 1
            && Height >= 1
            && Width <= 20000
            && Height <= 20000;
    }

    private sealed class ProbeBudget
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public void ThrowIfExpired()
        {
            if (_watch.Elapsed > TotalBudget)
            {
                throw new TimeoutException("PDF page-size probe budget expired.");
            }
        }
    }

    private sealed class PdfObjectIndex
    {
        private readonly Dictionary<PdfRef, PdfObject> _byRef;

        private PdfObjectIndex(Dictionary<PdfRef, PdfObject> byRef, List<PdfObject> inFileOrder)
        {
            _byRef = byRef;
            InFileOrder = inFileOrder;
        }

        public int Count => _byRef.Count;

        public IReadOnlyList<PdfObject> InFileOrder { get; }

        public static PdfObjectIndex Parse(string text, ProbeBudget budget)
        {
            var byRef = new Dictionary<PdfRef, PdfObject>();
            var inFileOrder = new List<PdfObject>();
            foreach (Match match in ObjectRegex.Matches(text))
            {
                budget.ThrowIfExpired();
                if (inFileOrder.Count >= MaxObjects)
                {
                    break;
                }

                var body = match.Groups["body"].Value;
                if (Encoding.Latin1.GetByteCount(body) > MaxObjectBodyBytes
                    || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || !int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
                {
                    continue;
                }

                var obj = new PdfObject(new PdfRef(number, generation), body, match.Index);
                byRef[obj.Ref] = obj;
                inFileOrder.Add(obj);
            }

            inFileOrder.Sort((left, right) => left.Position.CompareTo(right.Position));
            return new PdfObjectIndex(byRef, inFileOrder);
        }

        public bool TryGet(PdfRef reference, out PdfObject obj) => _byRef.TryGetValue(reference, out obj);
    }
}
