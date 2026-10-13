using System.Windows;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>One recognized word: where it is, as fractions of the picture, and the line it stands on.</summary>
internal sealed record PictureWord(string Text, Rect Box, int Line);

/// <summary>
/// The words Windows found in a picture, in reading order. Each word's box
/// is in the frame the engine read in: the picture turned back by
/// <see cref="Angle"/> about its center, so that the text runs level.
/// Positions asked about are given in the picture as it is shown, and are
/// turned into that frame here.
/// </summary>
internal sealed class PictureWords(IReadOnlyList<PictureWord> words, double angle, double aspect, string joiner)
{
    public static readonly PictureWords None = new([], 0, 1, " ");

    /// <summary>How far the engine found the text turned, clockwise, in degrees.</summary>
    public double Angle { get; } = angle;

    public int Count => words.Count;

    public PictureWord this[int index] => words[index];

    /// <summary>All of it: a line of text for each line in the picture.</summary>
    public string Text => Count == 0 ? string.Empty : TextOf(0, Count - 1);

    /// <summary>The words from one to another, whichever comes first, as they read.</summary>
    public string TextOf(int one, int other)
    {
        var (first, last) = Order(one, other);
        var text = new System.Text.StringBuilder();
        for (var i = first; i <= last; i++)
        {
            if (i > first)
            {
                text.Append(words[i].Line == words[i - 1].Line ? joiner : Environment.NewLine);
            }

            text.Append(words[i].Text);
        }

        return text.ToString();
    }

    /// <summary>One bar per line over the words from one to another, in the engine's frame.</summary>
    public IEnumerable<Rect> Highlights(int one, int other)
    {
        var (first, last) = Order(one, other);
        Rect? bar = null;
        for (var i = first; i <= last; i++)
        {
            if (bar is { } open && words[i].Line == words[i - 1].Line)
            {
                bar = Rect.Union(open, words[i].Box);
                continue;
            }

            if (bar is { } done)
            {
                yield return done;
            }

            bar = words[i].Box;
        }

        if (bar is { } end)
        {
            yield return end;
        }
    }

    /// <summary>The word under a point of the picture, with a little room around each word.</summary>
    public int? WordAt(Point point)
    {
        var at = ToEngineFrame(point);
        for (var i = 0; i < words.Count; i++)
        {
            var box = words[i].Box;
            box.Inflate(box.Height * 0.15 / aspect, box.Height * 0.15);
            if (box.Contains(at))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The word a drag has reached when it is over none: on the line
    /// nearest in height, the word nearest along it.
    /// </summary>
    public int Nearest(Point point)
    {
        var at = ToEngineFrame(point);
        var best = 0;
        var bestScore = double.MaxValue;
        for (var i = 0; i < words.Count; i++)
        {
            var box = words[i].Box;
            var dy = at.Y < box.Top ? box.Top - at.Y : at.Y > box.Bottom ? at.Y - box.Bottom : 0;
            var dx = at.X < box.Left ? box.Left - at.X : at.X > box.Right ? at.X - box.Right : 0;

            // Height decides the line; distance along it only breaks the tie.
            var score = dy * 1000 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// A point of the picture as shown, in the frame the boxes are in. The
    /// turn is made in pixels, where a degree is the same in both
    /// directions, and not in fractions, where it is not.
    /// </summary>
    private Point ToEngineFrame(Point point)
    {
        if (Angle == 0)
        {
            return point;
        }

        var radians = -Angle * Math.PI / 180;
        var x = (point.X - 0.5) * aspect;
        var y = point.Y - 0.5;
        return new Point(
            (x * Math.Cos(radians) - y * Math.Sin(radians)) / aspect + 0.5,
            x * Math.Sin(radians) + y * Math.Cos(radians) + 0.5);
    }

    private static (int First, int Last) Order(int one, int other) =>
        one <= other ? (one, other) : (other, one);
}

/// <summary>
/// The selection drawn over a picture. The viewer keeps it exactly over the
/// image, so the bars are drawn as fractions of its own size and follow
/// every zoom and pan.
/// </summary>
internal sealed class PictureTextLayer : FrameworkElement
{
    private PictureWords _words = PictureWords.None;
    private (int One, int Other)? _selection;

    public PictureTextLayer()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public PictureWords Words
    {
        get => _words;
        set
        {
            _words = value;
            _selection = null;
            InvalidateVisual();
        }
    }

    public (int One, int Other)? Selection
    {
        get => _selection;
        set
        {
            if (_selection != value)
            {
                _selection = value;
                InvalidateVisual();
            }
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_selection is not { } selection || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var accent = Theme.Current.Accent;
        var brush = new SolidColorBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B));
        brush.Freeze();
        if (_words.Angle != 0)
        {
            drawingContext.PushTransform(new RotateTransform(_words.Angle, ActualWidth / 2, ActualHeight / 2));
        }

        foreach (var bar in _words.Highlights(selection.One, selection.Other))
        {
            var rect = new Rect(bar.X * ActualWidth, bar.Y * ActualHeight, bar.Width * ActualWidth, bar.Height * ActualHeight);
            rect.Inflate(1, 1);
            drawingContext.DrawRoundedRectangle(brush, null, rect, 2, 2);
        }

        if (_words.Angle != 0)
        {
            drawingContext.Pop();
        }
    }
}
