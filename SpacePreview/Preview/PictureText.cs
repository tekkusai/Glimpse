using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SpacePreview.Preview;

/// <summary>
/// The text in a picture, read by the engine Windows ships. It runs on this
/// PC and nothing leaves it. It reads the languages the user has installed
/// in Windows; with none that it can read, there is no engine and the
/// viewer offers nothing.
/// </summary>
internal static class PictureText
{
    private static readonly Lazy<OcrEngine?> Engine = new(Create);

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
                    new System.Windows.Rect(box.X / width, box.Y / height, box.Width / width, box.Height / height),
                    line));
            }
        }

        // Chinese and Japanese run their characters together; the engine
        // reports each as a word.
        var language = engine.RecognizerLanguage.LanguageTag;
        var joiner = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : " ";
        return new PictureWords(words, result.TextAngle ?? 0, width / height, joiner);
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

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource picture)
    {
        // The engine refuses a side longer than its limit. A picture that
        // long is brought down to it; its text is large enough to survive.
        var longSide = Math.Max(picture.PixelWidth, picture.PixelHeight);
        if (longSide > OcrEngine.MaxImageDimension)
        {
            var scale = (double)OcrEngine.MaxImageDimension / longSide;
            picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        }

        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
    }
}
