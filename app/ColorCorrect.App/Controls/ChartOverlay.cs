using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ColorCorrect.Engine;

namespace ColorCorrect.App.Controls;

public sealed class ChartOverlay : Control
{
    public static readonly StyledProperty<HandshakeDocument?> HandshakeProperty =
        AvaloniaProperty.Register<ChartOverlay, HandshakeDocument?>(nameof(Handshake));

    public static readonly StyledProperty<int> FaceChoiceProperty =
        AvaloniaProperty.Register<ChartOverlay, int>(nameof(FaceChoice));

    public static readonly StyledProperty<double> CropProperty =
        AvaloniaProperty.Register<ChartOverlay, double>(nameof(Crop), 0.75);

    public static readonly StyledProperty<bool> UseAllFacesProperty =
        AvaloniaProperty.Register<ChartOverlay, bool>(nameof(UseAllFaces));

    public static readonly StyledProperty<bool> ShowLatticeProperty =
        AvaloniaProperty.Register<ChartOverlay, bool>(nameof(ShowLattice), true);

    public static readonly StyledProperty<bool> ShowSampleProperty =
        AvaloniaProperty.Register<ChartOverlay, bool>(nameof(ShowSample), true);

    public static readonly StyledProperty<bool> ShowDimProperty =
        AvaloniaProperty.Register<ChartOverlay, bool>(nameof(ShowDim), true);

    public static readonly StyledProperty<double> DisplayScaleProperty =
        AvaloniaProperty.Register<ChartOverlay, double>(nameof(DisplayScale), 1);

    public static readonly StyledProperty<double> OverlayScaleXProperty =
        AvaloniaProperty.Register<ChartOverlay, double>(nameof(OverlayScaleX), 1);

    public static readonly StyledProperty<double> OverlayScaleYProperty =
        AvaloniaProperty.Register<ChartOverlay, double>(nameof(OverlayScaleY), 1);

    public static readonly StyledProperty<bool> ShowPlaneProperty =
        AvaloniaProperty.Register<ChartOverlay, bool>(nameof(ShowPlane), true);

    public static readonly StyledProperty<Rect> PlaneRectProperty =
        AvaloniaProperty.Register<ChartOverlay, Rect>(nameof(PlaneRect));

    private IReadOnlyList<Quad> _faces = [];
    private IReadOnlyList<Quad> _lattice = [];
    private IReadOnlyList<Quad> _samples = [];
    private bool _cacheValid;
    private Geometry? _dimGeo;
    private readonly List<Geometry> _latticeGeo = [];
    private readonly List<Geometry> _sampleGeo = [];
    private readonly List<Geometry> _faceGeo = [];
    private double _geoSx = double.NaN;
    private double _geoSy = double.NaN;
    private Size _geoBounds;

    // PPT overlay palette: ~50% fill, stronger stroke so cells stay readable.
    private static readonly IBrush[] FaceStrokes =
    [
        new SolidColorBrush(Color.FromArgb(180, 40, 200, 40)),
        new SolidColorBrush(Color.FromArgb(180, 220, 180, 40)),
        new SolidColorBrush(Color.FromArgb(180, 40, 160, 220)),
        new SolidColorBrush(Color.FromArgb(180, 200, 80, 200)),
    ];
    private static readonly IBrush[] PatchStrokes =
    [
        new SolidColorBrush(Color.FromArgb(170, 90, 220, 90)),
        new SolidColorBrush(Color.FromArgb(170, 240, 210, 90)),
        new SolidColorBrush(Color.FromArgb(170, 90, 200, 240)),
        new SolidColorBrush(Color.FromArgb(170, 230, 140, 230)),
    ];
    private static readonly IBrush[] PatchFills =
    [
        new SolidColorBrush(Color.FromArgb(128, 90, 220, 90)),
        new SolidColorBrush(Color.FromArgb(128, 240, 210, 90)),
        new SolidColorBrush(Color.FromArgb(128, 90, 200, 240)),
        new SolidColorBrush(Color.FromArgb(128, 230, 140, 230)),
    ];
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(22, 8, 8, 10));
    private static readonly IBrush HaloInk = new SolidColorBrush(Color.FromArgb(70, 8, 10, 14));
    private static readonly IBrush CornerInk = new SolidColorBrush(Color.FromArgb(140, 16, 16, 18));
    private static readonly IBrush CornerFill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush PlaneFill = new SolidColorBrush(Color.FromArgb(18, 212, 165, 116));
    private static readonly IBrush PlaneStroke = new SolidColorBrush(Color.FromArgb(150, 212, 165, 116));

    static ChartOverlay()
    {
        AffectsRender<ChartOverlay>(
            HandshakeProperty, FaceChoiceProperty, CropProperty, UseAllFacesProperty,
            ShowLatticeProperty, ShowSampleProperty, ShowDimProperty, DisplayScaleProperty,
            OverlayScaleXProperty, OverlayScaleYProperty, ShowPlaneProperty, PlaneRectProperty);
    }

    public HandshakeDocument? Handshake
    {
        get => GetValue(HandshakeProperty);
        set => SetValue(HandshakeProperty, value);
    }

    public int FaceChoice
    {
        get => GetValue(FaceChoiceProperty);
        set => SetValue(FaceChoiceProperty, value);
    }

    public double Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public bool UseAllFaces
    {
        get => GetValue(UseAllFacesProperty);
        set => SetValue(UseAllFacesProperty, value);
    }

    public bool ShowLattice
    {
        get => GetValue(ShowLatticeProperty);
        set => SetValue(ShowLatticeProperty, value);
    }

    public bool ShowSample
    {
        get => GetValue(ShowSampleProperty);
        set => SetValue(ShowSampleProperty, value);
    }

    public bool ShowDim
    {
        get => GetValue(ShowDimProperty);
        set => SetValue(ShowDimProperty, value);
    }

    public double DisplayScale
    {
        get => GetValue(DisplayScaleProperty);
        set => SetValue(DisplayScaleProperty, value);
    }

    public double OverlayScaleX
    {
        get => GetValue(OverlayScaleXProperty);
        set => SetValue(OverlayScaleXProperty, value);
    }

    public double OverlayScaleY
    {
        get => GetValue(OverlayScaleYProperty);
        set => SetValue(OverlayScaleYProperty, value);
    }

    public bool ShowPlane
    {
        get => GetValue(ShowPlaneProperty);
        set => SetValue(ShowPlaneProperty, value);
    }

    public Rect PlaneRect
    {
        get => GetValue(PlaneRectProperty);
        set => SetValue(PlaneRectProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HandshakeProperty ||
            change.Property == FaceChoiceProperty ||
            change.Property == CropProperty ||
            change.Property == UseAllFacesProperty)
        {
            _cacheValid = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 2 || Bounds.Height < 2)
        {
            return;
        }

        var sx = OverlayScaleX > 0 ? OverlayScaleX : 1;
        var sy = OverlayScaleY > 0 ? OverlayScaleY : 1;
        var unit = 1.0 / Math.Max(0.04, DisplayScale);

        if (Handshake is not null)
        {
            RefreshCache();
            EnsureGeometries(sx, sy);

            if (ShowDim && _dimGeo is not null)
            {
                context.DrawGeometry(DimBrush, null, _dimGeo);
            }

            if (ShowLattice)
            {
                var perFace = FaceStride(_latticeGeo.Count, _faceGeo.Count);
                for (var i = 0; i < _latticeGeo.Count; i++)
                {
                    var tone = (perFace > 0 ? i / perFace : 0) % PatchStrokes.Length;
                    StrokeHalo(context, _latticeGeo[i], PatchStrokes[tone], 1.55 * unit);
                }
            }

            if (ShowSample)
            {
                var label = 11 * unit;
                var showIndex = DisplayScale >= 0.5 && (_samples.Count <= 36 || DisplayScale >= 0.7);
                var perFace = FaceStride(_sampleGeo.Count, _faceGeo.Count);
                for (var i = 0; i < _sampleGeo.Count; i++)
                {
                    var geo = _sampleGeo[i];
                    var tone = (perFace > 0 ? i / perFace : 0) % PatchStrokes.Length;
                    context.DrawGeometry(PatchFills[tone], null, geo);
                    StrokeHalo(context, geo, PatchStrokes[tone], 1.85 * unit);
                    if (showIndex && i < _samples.Count && ScreenMin(_samples[i], sx, sy) >= 16)
                    {
                        var (cx, cy) = Center(_samples[i], sx, sy);
                        PlotTheme.DrawText(context, (i + 1).ToString(), cx, cy - label * 0.45, label,
                            Color.FromArgb(210, 255, 255, 255), TextAlignment.Center, FontWeight.SemiBold);
                    }
                }
            }

            var cornerR = 4.6 * unit;
            for (var i = 0; i < _faceGeo.Count; i++)
            {
                StrokeHalo(context, _faceGeo[i], FaceStrokes[i % FaceStrokes.Length], 2.15 * unit);
                if (i >= _faces.Count)
                {
                    continue;
                }

                foreach (var p in _faces[i].Points)
                {
                    var pt = new Point(p.X * sx, p.Y * sy);
                    context.DrawEllipse(CornerInk, null, pt, cornerR + 0.7 * unit, cornerR + 0.7 * unit);
                    context.DrawEllipse(CornerFill, null, pt, cornerR, cornerR);
                }
            }
        }

        DrawPlane(context, sx, sy, unit);
    }

    private void DrawPlane(DrawingContext context, double sx, double sy, double unit)
    {
        if (!ShowPlane || PlaneRect.Width < 2 || PlaneRect.Height < 2)
        {
            return;
        }

        var rect = new Rect(PlaneRect.X * sx, PlaneRect.Y * sy, PlaneRect.Width * sx, PlaneRect.Height * sy);
        context.FillRectangle(PlaneFill, rect);
        var dash = new Pen(PlaneStroke, Math.Max(1.1, 1.35 * unit))
        {
            DashStyle = new DashStyle([5, 4], 0),
        };
        context.DrawRectangle(dash, rect);
        if (rect.Width >= 48 && rect.Height >= 20)
        {
            PlotTheme.DrawText(context, "FFC", rect.X + 6 * unit, rect.Y + 3 * unit, 10 * unit,
                Color.FromArgb(180, 212, 165, 116), TextAlignment.Left, FontWeight.SemiBold);
        }
    }

    private void RefreshCache()
    {
        if (_cacheValid)
        {
            return;
        }

        var handshake = Handshake;
        if (handshake is null)
        {
            _faces = [];
            _lattice = [];
            _samples = [];
            _cacheValid = true;
            return;
        }

        _faces = UseAllFaces ? handshake.AllFaceQuads() : FaceList(handshake.FaceQuad(FaceChoice));
        _lattice = UseAllFaces ? handshake.AllLatticeQuads() : handshake.LatticeQuads(FaceChoice);
        _samples = UseAllFaces ? handshake.AllSampleQuads(Crop) : handshake.SampleQuads(FaceChoice, Crop);
        _cacheValid = true;
        InvalidateGeometries();
    }

    private void InvalidateGeometries()
    {
        _geoSx = double.NaN;
        _dimGeo = null;
        _latticeGeo.Clear();
        _sampleGeo.Clear();
        _faceGeo.Clear();
    }

    private void EnsureGeometries(double sx, double sy)
    {
        var size = Bounds.Size;
        if (!double.IsNaN(_geoSx) && _geoSx == sx && _geoSy == sy && _geoBounds == size)
        {
            return;
        }

        _geoSx = sx;
        _geoSy = sy;
        _geoBounds = size;
        _latticeGeo.Clear();
        _sampleGeo.Clear();
        _faceGeo.Clear();
        foreach (var quad in _lattice)
        {
            _latticeGeo.Add(QuadGeometry(quad, sx, sy));
        }

        foreach (var quad in _samples)
        {
            _sampleGeo.Add(QuadGeometry(quad, sx, sy));
        }

        foreach (var face in _faces)
        {
            _faceGeo.Add(QuadGeometry(face, sx, sy));
        }

        _dimGeo = _faces.Count == 0 ? null : DimGeometry(sx, sy);
    }

    private Geometry DimGeometry(double sx, double sy)
    {
        var figures = new PathFigures();
        figures.Add(RectFigure(Bounds));
        foreach (var face in _faces)
        {
            figures.Add(QuadFigure(face, sx, sy));
        }

        return new PathGeometry
        {
            FillRule = FillRule.EvenOdd,
            Figures = figures,
        };
    }

    private static PathFigure RectFigure(Rect bounds)
    {
        return new PathFigure
        {
            StartPoint = bounds.TopLeft,
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegments
            {
                new LineSegment { Point = bounds.TopRight },
                new LineSegment { Point = bounds.BottomRight },
                new LineSegment { Point = bounds.BottomLeft },
            },
        };
    }

    private static PathFigure QuadFigure(Quad quad, double sx, double sy)
    {
        return new PathFigure
        {
            StartPoint = new Point(quad.A.X * sx, quad.A.Y * sy),
            IsClosed = true,
            IsFilled = true,
            Segments = new PathSegments
            {
                new LineSegment { Point = new Point(quad.B.X * sx, quad.B.Y * sy) },
                new LineSegment { Point = new Point(quad.C.X * sx, quad.C.Y * sy) },
                new LineSegment { Point = new Point(quad.D.X * sx, quad.D.Y * sy) },
            },
        };
    }

    private static void StrokeHalo(DrawingContext context, Geometry geo, IBrush color, double width)
    {
        context.DrawGeometry(null, new Pen(HaloInk, width + Math.Max(0.6, width * 0.35)), geo);
        context.DrawGeometry(null, new Pen(color, width), geo);
    }

    private double ScreenMin(Quad quad, double sx, double sy)
    {
        var minX = Math.Min(Math.Min(quad.A.X, quad.B.X), Math.Min(quad.C.X, quad.D.X)) * sx;
        var maxX = Math.Max(Math.Max(quad.A.X, quad.B.X), Math.Max(quad.C.X, quad.D.X)) * sx;
        var minY = Math.Min(Math.Min(quad.A.Y, quad.B.Y), Math.Min(quad.C.Y, quad.D.Y)) * sy;
        var maxY = Math.Max(Math.Max(quad.A.Y, quad.B.Y), Math.Max(quad.C.Y, quad.D.Y)) * sy;
        return Math.Min(maxX - minX, maxY - minY) * DisplayScale;
    }

    private static (double X, double Y) Center(Quad quad, double sx, double sy) =>
        (((quad.A.X + quad.B.X + quad.C.X + quad.D.X) / 4) * sx,
            ((quad.A.Y + quad.B.Y + quad.C.Y + quad.D.Y) / 4) * sy);

    private static IReadOnlyList<Quad> FaceList(Quad? quad) => quad is null ? [] : [quad];

    private static int FaceStride(int count, int faces) =>
        faces > 1 && count >= faces ? Math.Max(1, count / faces) : count;

    private static Geometry QuadGeometry(Quad quad, double sx, double sy)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        var first = true;
        foreach (var p in quad.Points)
        {
            var pt = new Point(p.X * sx, p.Y * sy);
            if (first)
            {
                ctx.BeginFigure(pt, true);
                first = false;
            }
            else
            {
                ctx.LineTo(pt);
            }
        }

        ctx.EndFigure(true);
        return geo;
    }
}
