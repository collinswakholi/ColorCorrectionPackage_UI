using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.VisualTree;

namespace ColorCorrect.App.Controls;

/// <summary>
/// Runs a looping style animation only while its element can be seen.
/// <para>
/// Avalonia keeps an infinite style animation running for every element in the visual
/// tree, visible or not, and each tick schedules a new frame. The hidden spinners and the
/// progress bars' sheen kept the idle app at about 30 % of a CPU core. With
/// <c>VisibleMotion.Enabled</c> set, the element carries the <c>motion</c> class only while it
/// is in the tree and it and all its ancestors are visible; looping animations select on
/// that class.
/// </para>
/// </summary>
public static class VisibleMotion
{
    public const string MotionClass = "motion";

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<StyledElement, bool>("Enabled", typeof(VisibleMotion));

    // The element and its ancestors whose IsVisible we listen to, while attached.
    private static readonly ConditionalWeakTable<Visual, List<Visual>> Watched = new();

    static VisibleMotion()
    {
        EnabledProperty.Changed.AddClassHandler<Visual>(OnEnabledChanged);
    }

    public static bool GetEnabled(StyledElement element) => element.GetValue(EnabledProperty);

    public static void SetEnabled(StyledElement element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(Visual visual, AvaloniaPropertyChangedEventArgs e)
    {
        visual.AttachedToVisualTree -= OnAttached;
        visual.DetachedFromVisualTree -= OnDetached;
        Unwatch(visual);
        if (e.GetNewValue<bool>())
        {
            visual.AttachedToVisualTree += OnAttached;
            visual.DetachedFromVisualTree += OnDetached;
            if (visual.IsAttachedToVisualTree())
            {
                Watch(visual);
            }
        }
        Sync(visual);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual)
        {
            Watch(visual);
            Sync(visual);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Visual visual)
        {
            Unwatch(visual);
            Sync(visual);
        }
    }

    private static void Watch(Visual visual)
    {
        Unwatch(visual);
        var chain = new List<Visual>();
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            chain.Add(v);
            v.PropertyChanged += OnChainPropertyChanged;
        }
        Watched.AddOrUpdate(visual, chain);

        void OnChainPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty)
            {
                Sync(visual);
            }
        }

        Handlers.AddOrUpdate(visual, OnChainPropertyChanged);
    }

    private static readonly ConditionalWeakTable<Visual, System.EventHandler<AvaloniaPropertyChangedEventArgs>> Handlers = new();

    private static void Unwatch(Visual visual)
    {
        if (Watched.TryGetValue(visual, out var chain) && Handlers.TryGetValue(visual, out var handler))
        {
            foreach (var v in chain)
            {
                v.PropertyChanged -= handler;
            }
        }
        Watched.Remove(visual);
        Handlers.Remove(visual);
    }

    private static void Sync(Visual visual)
    {
        var running = visual.GetValue(EnabledProperty) && visual.IsAttachedToVisualTree() && ChainVisible(visual);
        visual.Classes.Set(MotionClass, running);
    }

    private static bool ChainVisible(Visual visual)
    {
        for (Visual? v = visual; v is not null; v = v.GetVisualParent())
        {
            if (!v.IsVisible)
            {
                return false;
            }
        }
        return true;
    }
}
