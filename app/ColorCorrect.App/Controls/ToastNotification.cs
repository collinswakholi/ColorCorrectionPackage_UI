using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ColorCorrect.App.Services;

namespace ColorCorrect.App.Controls;

public sealed class ToastNotification : UserControl
{
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) SuccessDark =
        (Color.FromRgb(20, 50, 35), Color.FromRgb(16, 185, 129), Color.FromRgb(134, 239, 172), "✓");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) ErrorDark =
        (Color.FromRgb(60, 25, 25), Color.FromRgb(239, 68, 68), Color.FromRgb(252, 165, 165), "×");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) WarningDark =
        (Color.FromRgb(55, 43, 18), Color.FromRgb(245, 158, 11), Color.FromRgb(253, 230, 138), "!");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) InfoDark =
        (Color.FromRgb(23, 37, 70), Color.FromRgb(47, 111, 98), Color.FromRgb(191, 219, 254), "i");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) SuccessLight =
        (Color.FromRgb(236, 253, 245), Color.FromRgb(4, 120, 87), Color.FromRgb(6, 95, 70), "✓");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) ErrorLight =
        (Color.FromRgb(254, 226, 226), Color.FromRgb(220, 38, 38), Color.FromRgb(153, 27, 27), "×");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) WarningLight =
        (Color.FromRgb(255, 251, 235), Color.FromRgb(180, 83, 9), Color.FromRgb(120, 53, 15), "!");
    private static readonly (Color Bg, Color Accent, Color Text, string Symbol) InfoLight =
        (Color.FromRgb(228, 239, 235), Color.FromRgb(47, 111, 98), Color.FromRgb(20, 50, 44), "i");

    public event EventHandler? Dismissed;

    private readonly Control _root;
    private bool _dismissing;

    public ToastNotification(ToastRequest request)
    {
        MinWidth = 0;
        MaxWidth = 360;
        MinHeight = 48;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(this, request.Message);

        var light = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        var theme = request.Type switch
        {
            ToastType.Success => light ? SuccessLight : SuccessDark,
            ToastType.Error => light ? ErrorLight : ErrorDark,
            ToastType.Warning => light ? WarningLight : WarningDark,
            _ => light ? InfoLight : InfoDark,
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };

        var iconBg = new Border
        {
            Background = new SolidColorBrush(theme.Accent),
            CornerRadius = new CornerRadius(6),
            Width = 28,
            Height = 28,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = theme.Symbol,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(iconBg, 0);
        grid.Children.Add(iconBg);

        var msg = new TextBlock
        {
            Text = request.Message,
            Foreground = new SolidColorBrush(theme.Text),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(msg, 1);
        grid.Children.Add(msg);

        var closeBtn = new Button
        {
            Content = "×",
            FontSize = 16,
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = new SolidColorBrush(theme.Text),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        AutomationProperties.SetName(closeBtn, "Dismiss notification");
        closeBtn.Click += (_, _) => BeginDismiss();
        Grid.SetColumn(closeBtn, 2);
        grid.Children.Add(closeBtn);

        var innerPb = new Border
        {
            Background = new SolidColorBrush(theme.Accent),
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            RenderTransform = new ScaleTransform(1, 1),
        };

        var body = new DockPanel();
        DockPanel.SetDock(innerPb, Dock.Bottom);
        body.Children.Add(innerPb);
        body.Children.Add(grid);

        var border = new Border
        {
            Background = new SolidColorBrush(theme.Bg),
            BorderBrush = new SolidColorBrush(theme.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 0),
            ClipToBounds = true,
            Child = body,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 16,
                OffsetY = 4,
                Color = Color.FromArgb(60, 0, 0, 0),
            }),
        };

        var host = new Border { Child = border, Opacity = 0 };
        _root = host;
        Content = host;

        DispatcherTimer.RunOnce(() =>
        {
            _root.Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(280) },
            ];
            _root.Opacity = 1;
            if (innerPb.RenderTransform is ScaleTransform scale)
            {
                scale.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = ScaleTransform.ScaleXProperty,
                        Duration = TimeSpan.FromMilliseconds(request.DurationMs),
                    },
                ];
                scale.ScaleX = 0;
            }
        }, TimeSpan.FromMilliseconds(16));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(request.DurationMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            BeginDismiss();
        };
        timer.Start();
    }

    public async void BeginDismiss()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        _root.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) },
        ];
        _root.Opacity = 0;
        await Task.Delay(250);
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
