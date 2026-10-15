using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ColorCorrect.App.Controls;

public sealed class ZoomPanView : Panel
{
    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ZoomPanView, double>(nameof(Scale), 1);

    public static readonly StyledProperty<string> ScaleLabelProperty =
        AvaloniaProperty.Register<ZoomPanView, string>(nameof(ScaleLabel), "Fit");

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _translate = new();
    private readonly TransformGroup _transform = new();
    private Control? _content;
    private bool _fitted = true;
    private bool _panning;
    private Point _last;
    private double _fitScale = 1;

    public ZoomPanView()
    {
        ClipToBounds = true;
        Focusable = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        _transform.Children.Add(_scale);
        _transform.Children.Add(_translate);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPress, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMove, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRelease, RoutingStrategies.Tunnel);
        DoubleTapped += (_, e) =>
        {
            Fit();
            e.Handled = true;
        };
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        private set => SetValue(ScaleProperty, value);
    }

    public string ScaleLabel
    {
        get => GetValue(ScaleLabelProperty);
        private set => SetValue(ScaleLabelProperty, value);
    }

    public bool DidPan { get; private set; }

    public void Fit() => Fit(Bounds.Size);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count > 0)
        {
            Children[0].Measure(Size.Infinity);
        }

        var w = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : 0;
        var h = double.IsFinite(availableSize.Height) ? Math.Max(0, availableSize.Height) : 0;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count > 0)
        {
            EnsureContent(Children[0]);
            var child = Children[0];
            var size = child.DesiredSize;
            if (size.Width < 1 || size.Height < 1)
            {
                size = new Size(
                    double.IsFinite(child.Width) && child.Width > 0 ? child.Width : 1,
                    double.IsFinite(child.Height) && child.Height > 0 ? child.Height : 1);
            }

            child.Arrange(new Rect(new Point(0, 0), size));
        }

        if (_fitted)
        {
            Fit(finalSize);
        }
        else
        {
            Clamp(finalSize);
        }

        return finalSize;
    }

    private void EnsureContent(Control child)
    {
        if (ReferenceEquals(_content, child))
        {
            return;
        }

        if (_content is not null && ReferenceEquals(_content.RenderTransform, _transform))
        {
            _content.RenderTransform = null;
        }

        _content = child;
        child.RenderTransform = _transform;
        child.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        _fitted = true;
    }

    private void Fit(Size viewport)
    {
        var size = ContentSize();
        if (viewport.Width < 2 || viewport.Height < 2 || size.Width < 1 || size.Height < 1)
        {
            return;
        }

        var scale = Math.Min(viewport.Width / size.Width, viewport.Height / size.Height);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        _fitScale = scale;
        _scale.ScaleX = _scale.ScaleY = scale;
        _translate.X = (viewport.Width - size.Width * scale) / 2;
        _translate.Y = (viewport.Height - size.Height * scale) / 2;
        _fitted = true;
        DidPan = false;
        PublishScale();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        var delta = Math.Abs(e.Delta.Y) > 0.01 ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        ZoomAt(e.GetPosition(this), Math.Pow(1.12, delta));
        e.Handled = true;
    }

    private void OnPress(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        DidPan = false;
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _panning = true;
            _last = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            _panning = true;
            _last = point.Position;
        }
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _last;
        if (!DidPan && Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 3)
        {
            return;
        }

        _last = pos;
        _translate.X += delta.X;
        _translate.Y += delta.Y;
        _fitted = false;
        DidPan = true;
        e.Pointer.Capture(this);
        Clamp(Bounds.Size);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void OnRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning)
        {
            return;
        }

        _panning = false;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        if (DidPan)
        {
            e.Handled = true;
        }
    }

    private void ZoomAt(Point view, double factor)
    {
        var size = ContentSize();
        var old = _scale.ScaleX;
        if (old <= 0 || size.Width < 1)
        {
            return;
        }

        var min = Math.Max(0.04, _fitScale * 0.4);
        var next = Math.Clamp(old * factor, min, 24);
        if (Math.Abs(next - old) < 1e-6)
        {
            return;
        }

        var contentX = (view.X - _translate.X) / old;
        var contentY = (view.Y - _translate.Y) / old;
        _scale.ScaleX = _scale.ScaleY = next;
        _translate.X = view.X - contentX * next;
        _translate.Y = view.Y - contentY * next;
        _fitted = Math.Abs(next - _fitScale) < 0.002;
        Clamp(Bounds.Size);
    }

    private void Clamp(Size viewport)
    {
        var size = ContentSize();
        var w = size.Width * _scale.ScaleX;
        var h = size.Height * _scale.ScaleY;
        const double slack = 48;
        if (w <= viewport.Width)
        {
            _translate.X = (viewport.Width - w) / 2;
        }
        else
        {
            _translate.X = Math.Clamp(_translate.X, viewport.Width - w - slack, slack);
        }

        if (h <= viewport.Height)
        {
            _translate.Y = (viewport.Height - h) / 2;
        }
        else
        {
            _translate.Y = Math.Clamp(_translate.Y, viewport.Height - h - slack, slack);
        }

        PublishScale();
    }

    private void PublishScale()
    {
        Scale = _scale.ScaleX;
        var pct = _scale.ScaleX * 100;
        ScaleLabel = _fitted ? $"Fit · {pct:0}%" : $"{pct:0}%";
    }

    private Size ContentSize()
    {
        if (_content is null)
        {
            return new Size(1, 1);
        }

        var w = _content.Bounds.Width > 1 ? _content.Bounds.Width : _content.DesiredSize.Width;
        var h = _content.Bounds.Height > 1 ? _content.Bounds.Height : _content.DesiredSize.Height;
        if (w < 1 && double.IsFinite(_content.Width))
        {
            w = _content.Width;
        }

        if (h < 1 && double.IsFinite(_content.Height))
        {
            h = _content.Height;
        }

        if (!double.IsFinite(w) || !double.IsFinite(h) || w < 1 || h < 1)
        {
            return new Size(1, 1);
        }

        return new Size(w, h);
    }
}
