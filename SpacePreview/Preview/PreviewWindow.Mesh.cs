using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    private static FrameworkElement BuildMeshPreview(MeshPreview preview)
    {
        var grid = new Grid
        {
            Background = Theme.Brush(Theme.Current.CanvasBg),
            ClipToBounds = true
        };

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

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(69, 165, 255))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(215, 240, 255)), 34));

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
        scene.Children.Add(new AmbientLight(Color.FromRgb(70, 74, 84)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(246, 248, 255), new Vector3D(-0.55, 0.85, -1.1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(74, 132, 255), new Vector3D(0.75, -0.35, -0.45)));
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
            CornerRadius = Metrics.PillCorner,
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
}
