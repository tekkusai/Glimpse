using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private static FrameworkElement BuildMeshPreview(MeshPreview preview)
    {
        var grid = new Grid { ClipToBounds = true }.Themed(Panel.BackgroundProperty, "Theme.CanvasBg");

        var viewport = new Viewport3D
        {
            ClipToBounds = true
        };

        var camera = new PerspectiveCamera
        {
            Position = new Point3D(4.8, -6.4, 3.9),
            LookDirection = new Vector3D(-4.8, 6.4, -3.9),
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = 34
        };
        viewport.Camera = camera;

        // Light clay tinted a quarter toward the accent, the same in both
        // themes: shading carries the form on either canvas.
        var body = new DiffuseMaterial();
        Theme.Follow(grid, () => body.Brush = Theme.Brush(Blend(Theme.Dark.TextSecondary, Theme.Current.Accent, 0.25)));
        var material = new MaterialGroup();
        material.Children.Add(body);
        material.Children.Add(new SpecularMaterial(Theme.Brush(Color.FromRgb(0x30, 0x30, 0x30)), 40));

        var yaw = new AxisAngleRotation3D(new Vector3D(0, 0, 1), -18);
        var pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -8);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(pitch));
        transform.Children.Add(new RotateTransform3D(yaw));

        var model = new GeometryModel3D(preview.Mesh, material)
        {
            BackMaterial = material,
            Transform = transform
        };

        var scene = new Model3DGroup();
        // White key light from the upper left, a dim neutral fill opposite it.
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x38, 0x38, 0x38)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xE8, 0xE8), new Vector3D(-0.7, 0.45, -0.55)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x40, 0x40, 0x40), new Vector3D(0.8, 0.2, -0.3)));
        scene.Children.Add(model);

        viewport.Children.Add(new ModelVisual3D { Content = scene });
        grid.Children.Add(viewport);

        var triangleText = preview.CountLabel;
        var overlay = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18),
            Padding = new Thickness(12, 8, 12, 9),
            CornerRadius = Metrics.SurfaceCorner,
            Background = Theme.Brush(Theme.OverlayScrim),
            BorderBrush = Theme.Brush(Theme.OverlayBorder),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = preview.FormatName,
                        FontSize = Metrics.FontLabel,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Theme.Brush(Theme.OverlayText)
                    },
                    new TextBlock
                    {
                        Text = triangleText,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontSize = Metrics.FontCaption,
                        Foreground = Theme.Brush(Theme.OverlayTextSecondary)
                    },
                    new TextBlock
                    {
                        Text = "Drag to rotate · wheel to zoom",
                        Margin = new Thickness(0, 5, 0, 0),
                        FontSize = Metrics.FontCaption,
                        Foreground = Theme.Brush(Theme.OverlayTextTertiary)
                    }
                }
            }
        };
        grid.Children.Add(overlay);

        // Handlers live on the grid, not the viewport: a Viewport3D only
        // hit-tests where geometry actually renders, which would make empty
        // space around the model mouse-transparent. The whole preview area
        // should rotate and zoom.
        var lastPosition = new Point();
        var isDragging = false;
        grid.MouseLeftButtonDown += (_, e) =>
        {
            isDragging = true;
            lastPosition = e.GetPosition(grid);
            grid.CaptureMouse();
            grid.Cursor = Cursors.SizeAll;
            e.Handled = true;
        };
        grid.MouseLeftButtonUp += (_, e) =>
        {
            isDragging = false;
            grid.ReleaseMouseCapture();
            grid.Cursor = Cursors.Arrow;
            e.Handled = true;
        };
        grid.MouseMove += (_, e) =>
        {
            if (!isDragging)
            {
                return;
            }

            var position = e.GetPosition(grid);
            var delta = position - lastPosition;
            yaw.Angle += delta.X * 0.42;
            pitch.Angle = Math.Clamp(pitch.Angle + delta.Y * 0.32, -85, 85);
            lastPosition = position;
            e.Handled = true;
        };
        grid.MouseWheel += (_, e) =>
        {
            var factor = e.Delta > 0 ? 0.88 : 1.14;
            var position = camera.Position;
            var vector = new Vector3D(position.X * factor, position.Y * factor, position.Z * factor);
            var length = vector.Length;
            if (length is >= 2.2 and <= 15)
            {
                camera.Position = new Point3D(vector.X, vector.Y, vector.Z);
                camera.LookDirection = new Vector3D(-vector.X, -vector.Y, -vector.Z);
            }

            e.Handled = true;
        };
        // The capture handles the drag: rotation keeps following the pointer
        // wherever it goes, and ends on button-up or when the system takes
        // the capture away (e.g. a window resize starting). A MouseLeave
        // cancel here would kill drags against stale over-state bookkeeping.
        grid.LostMouseCapture += (_, _) =>
        {
            isDragging = false;
            grid.Cursor = Cursors.Arrow;
        };

        return grid;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>
    /// Dev probe: spins the mesh on screen for two seconds, one step per
    /// frame, and reports the frame intervals WPF achieved.
    /// </summary>
    internal async Task<string> ProbeMeshSpinAsync()
    {
        if (FindVisualChild<Viewport3D>(PreviewContent) is not { } viewport
            || viewport.Children.OfType<ModelVisual3D>().FirstOrDefault()?.Content is not Model3DGroup scene
            || scene.Children.OfType<GeometryModel3D>().FirstOrDefault() is not { Transform: Transform3DGroup transform }
            || transform.Children.OfType<RotateTransform3D>().LastOrDefault()?.Rotation is not AxisAngleRotation3D yaw)
        {
            return "no mesh";
        }

        var frames = new List<TimeSpan>();
        var done = new TaskCompletionSource();
        var started = DateTime.UtcNow;
        void OnRendering(object? sender, EventArgs e)
        {
            frames.Add(((RenderingEventArgs)e).RenderingTime);
            yaw.Angle += 3;
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
            {
                CompositionTarget.Rendering -= OnRendering;
                done.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        await done.Task;
        var intervals = frames.Zip(frames.Skip(1), (a, b) => (b - a).TotalMilliseconds).Where(ms => ms > 0).Order().ToList();
        if (intervals.Count == 0)
        {
            return "no frames";
        }

        var median = intervals[intervals.Count / 2];
        return $"frames={intervals.Count + 1} median={median:0.0}ms p95={intervals[(int)(intervals.Count * 0.95)]:0.0}ms max={intervals[^1]:0.0}ms slow={intervals.Count(ms => ms > median * 1.5)}";
    }
}
