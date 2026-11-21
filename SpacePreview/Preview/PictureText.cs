using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinRT;

namespace SpacePreview.Preview;

/// <summary>
/// The text in a picture, read by the engine Windows ships. It runs on this
/// PC and nothing leaves it. It reads the languages the user has installed
/// in Windows; with none that it can read, there is no engine and the
/// viewer offers nothing.
/// </summary>
internal static class PictureText
{
    // Windows 11 raised OcrEngine.MaxImageDimension to 10000, which would
    // hand full 4096 px decodes (58+ MiB) to OCR. Capping the OCR copy at
    // 2600 px keeps text crisp while cutting native buffer size and OCR time
    // by more than half.
    internal const int MaxOcrDimension = 2600;

    private static readonly Guid MemoryBufferByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
    private static readonly Lazy<OcrEngine?> Engine = new(Create);

    // Weakly keyed by the decoded BitmapSource so flipping back and forth
    // between cached images reuses recognized words without re-running OCR,
    // and entries disappear automatically when the bitmap is evicted or closed.
    private static readonly ConditionalWeakTable<BitmapSource, PictureWords> RecognizedWords = new();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferDelegate(IntPtr self, out IntPtr buffer, out uint capacity);

    /// <summary>Asks Windows once. The first answer takes milliseconds.</summary>
    public static bool IsAvailable => Engine.Value is not null;

    /// <summary>
    /// The picture's lines of text, top to bottom, or an empty string when
    /// it holds none. The picture is read as it is shown, so one the
    /// viewer has turned upright is read upright.
    /// </summary>
    public static async Task<string> ReadAsync(BitmapSource picture, CancellationToken token = default) =>
        (await RecognizeAsync(picture, token)).Text;

    /// <summary>Every word in the picture with the place it was found.</summary>
    public static async Task<PictureWords> RecognizeAsync(BitmapSource picture, CancellationToken token = default)
    {
        if (RecognizedWords.TryGetValue(picture, out var cached))
        {
            return cached;
        }

        // The engine is made away from the caller's thread: the first
        // answer loads it.
        if (await Task.Run(() => Engine.Value, token) is not { } engine)
        {
            return PictureWords.None;
        }

        using var bitmap = await Task.Run(() => ToSoftwareBitmap(picture), token);
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        double width = bitmap.PixelWidth;
        double height = bitmap.PixelHeight;
        var words = new List<PictureWord>();
        for (var line = 0; line < result.Lines.Count; line++)
        {
            foreach (var word in result.Lines[line].Words)
            {
                var box = word.BoundingRect;
                words.Add(new PictureWord(
                    word.Text,
                    new Rect(box.X / width, box.Y / height, box.Width / width, box.Height / height),
                    line));
            }
        }

        // Chinese and Japanese run their characters together; the engine
        // reports each as a word.
        var language = engine.RecognizerLanguage.LanguageTag;
        var joiner = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : " ";
        var recognized = new PictureWords(words, result.TextAngle ?? 0, width / height, joiner);
        RecognizedWords.AddOrUpdate(picture, recognized);
        return recognized;
    }

    private static OcrEngine? Create()
    {
        try
        {
            // The user's own languages first. A profile in a language the
            // engine cannot read still gets any language it can.
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? (OcrEngine.AvailableRecognizerLanguages.FirstOrDefault() is { } language
                    ? OcrEngine.TryCreateFromLanguage(language)
                    : null);
        }
        catch (Exception ex)
        {
            Services.DiagnosticLog.Write($"picture text engine unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static SoftwareBitmap ToSoftwareBitmap(BitmapSource picture)
    {
        var maxDimension = (int)Math.Min(OcrEngine.MaxImageDimension, MaxOcrDimension);
        var longSide = Math.Max(picture.PixelWidth, picture.PixelHeight);
        if (longSide > maxDimension)
        {
            var scale = (double)maxDimension / longSide;
            picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        }

        var converted = picture.Format == PixelFormats.Bgra32
            ? picture
            : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;

        // Copy directly into the native SoftwareBitmap buffer so OCR never
        // allocates a multi-megabyte managed byte[] on the Large Object Heap
        // or pins one behind a WinRT IBuffer CCW.
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        try
        {
            using var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            var plane = buffer.GetPlaneDescription(0);
            var iid = MemoryBufferByteAccessIid;
            var hr = Marshal.QueryInterface(((IWinRTObject)reference).NativeObject.ThisPtr, in iid, out var byteAccess);
            if (hr != 0 || byteAccess == IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(hr != 0 ? hr : unchecked((int)0x80004002));
            }

            try
            {
                var vtable = Marshal.ReadIntPtr(byteAccess);
                var getBufferPtr = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                var getBuffer = Marshal.GetDelegateForFunctionPointer<GetBufferDelegate>(getBufferPtr);
                hr = getBuffer(byteAccess, out var data, out var capacity);
                if (hr != 0 || data == IntPtr.Zero)
                {
                    Marshal.ThrowExceptionForHR(hr != 0 ? hr : unchecked((int)0x80004005));
                }

                var available = checked((int)capacity - plane.StartIndex);
                converted.CopyPixels(Int32Rect.Empty, IntPtr.Add(data, plane.StartIndex), available, plane.Stride);
            }
            finally
            {
                Marshal.Release(byteAccess);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
