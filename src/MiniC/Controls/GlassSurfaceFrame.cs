using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace MiniC.Controls;

/// <summary>
/// Directional reflections along a glass bevel and a fine contrast contour.
/// </summary>
public sealed class GlassSurfaceFrame : FrameworkElement
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(GlassSurfaceFrame),
        new FrameworkPropertyMetadata(GroupGeometry.Radius, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(
        nameof(Strength), typeof(double), typeof(GlassSurfaceFrame),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(GlassSurfaceFrame),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FrostedProperty = DependencyProperty.Register(
        nameof(Frosted), typeof(bool), typeof(GlassSurfaceFrame),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Frosted { get => (bool)GetValue(FrostedProperty); set => SetValue(FrostedProperty, value); }

    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    public double Radius
    {
        get => (double)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public double Strength
    {
        get => (double)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public GlassSurfaceFrame()
    {
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 2 || ActualHeight <= 2) return;

        var strength = Math.Clamp(double.IsFinite(Strength) ? Strength : 1, 0.65, 1.5);
        var radius = Math.Clamp(double.IsFinite(Radius) ? Radius : GroupGeometry.Radius, 0,
            Math.Min(ActualWidth, ActualHeight) / 2);

        var radians = ((double.IsFinite(Angle) ? Angle : 0) - 135) * Math.PI / 180;
        var light = new Vector(Math.Cos(radians) * 0.5, Math.Sin(radians) * 0.5);
        var reflection = new LinearGradientBrush
        {
            StartPoint = new Point(0.5 + light.X, 0.5 + light.Y),
            EndPoint = new Point(0.5 - light.X, 0.5 - light.Y)
        };
        reflection.GradientStops.Add(new GradientStop(Color.FromArgb((byte)((Frosted ? 145 : 170) * strength), 255, 255, 255), 0));
        reflection.GradientStops.Add(new GradientStop(Color.FromArgb(35, 255, 255, 255), 0.42));
        reflection.GradientStops.Add(new GradientStop(Color.FromArgb(Frosted ? (byte)25 : (byte)65, 12, 18, 26), 0.72));
        reflection.GradientStops.Add(new GradientStop(Color.FromArgb(Frosted ? (byte)45 : (byte)95, 255, 255, 255), 1));
        reflection.Freeze();
        // A fine contour distinguishes white imagery; reflections vary with light.
        Stroke(drawingContext, new SolidColorBrush(Color.FromArgb(Frosted ? (byte)40 : (byte)85, 16, 22, 30)), 0.8, 0.6, radius);
        Stroke(drawingContext, reflection, Frosted ? 2.2 : 1.4, 1.6, radius);
    }

    private void Stroke(DrawingContext dc, System.Windows.Media.Brush brush, double width, double inset, double radius)
    {
        if (ActualWidth <= inset * 2 || ActualHeight <= inset * 2) return;
        var pen = new Pen(brush, width);
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen,
            new Rect(inset, inset, ActualWidth - inset * 2, ActualHeight - inset * 2),
            Math.Max(0, radius - inset), Math.Max(0, radius - inset));
    }
}
