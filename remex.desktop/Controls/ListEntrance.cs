using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Remex.Desktop.Services;
using Remex.Desktop.Styles;

namespace Remex.Desktop.Controls;

/// <summary>
/// Fades and slides in the rows a list GAINS, with a short capped stagger (RemEx-pp4cm.1).
/// Opt in per list: <c>ctrl:ListEntrance.IsEnabled="True"</c> on an <see cref="ItemsControl"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHAT ANIMATES. Only rows that arrive through an <c>Add</c> to the bound collection after the
/// list is on screen — a routine created, a device paired, a sensor pinned, a log line written —
/// and only when that burst of additions is small (<see cref="Motion.MaxAnimatedBatch"/>). A burst
/// is everything added before the next layout pass, so a view model that fills a list in a loop is
/// one burst, not a cascade.
/// </para>
/// <para>
/// WHAT DOES NOT. The rows a list shows when its page opens (the page's own shared-axis entrance
/// already covers them, and replaying a row cascade on every visit reads as repetition); a
/// <c>Reset</c> or a replaced <c>ItemsSource</c> (a reload); a burst bigger than the cap; a
/// container recycled by a virtualizing panel as the user scrolls (it is prepared for an item that
/// was never just added); anything under reduced motion; anything while the window is minimised or
/// hidden in the tray.
/// </para>
/// <para>
/// HOW. Key frames on <c>Opacity</c> and <c>TranslateTransform.Y</c> — never on
/// <c>RenderTransform</c> itself, which has no key-frame animator and crashes before first paint
/// (RemEx-qolhg). The backward fill holds a staggered row invisible until its turn. Both properties
/// are cleared when the animation ends or the container is recycled, so a row never keeps a local
/// value that would outrank its styles.
/// </para>
/// <para>
/// Removal is not animated: an <c>ItemsControl</c> drops a removed row's container in the same
/// layout pass, and holding it back would mean every view model deferring its removes.
/// </para>
/// </remarks>
public sealed class ListEntrance : AvaloniaObject
{
    /// <summary>Turns the entrance on for an <see cref="ItemsControl"/>.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ListEntrance, ItemsControl, bool>("IsEnabled");

    private static readonly ConditionalWeakTable<ItemsControl, State> States = new();
    private static readonly ConditionalWeakTable<Control, CancellationTokenSource> Running = new();

    static ListEntrance()
    {
        IsEnabledProperty.Changed.AddClassHandler<ItemsControl>(OnIsEnabledChanged);
    }

    private ListEntrance()
    {
    }

    /// <summary>Gets <see cref="IsEnabledProperty"/>.</summary>
    public static bool GetIsEnabled(ItemsControl control) => control.GetValue(IsEnabledProperty);

    /// <summary>Sets <see cref="IsEnabledProperty"/>.</summary>
    public static void SetIsEnabled(ItemsControl control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ItemsControl list, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            if (States.TryGetValue(list, out _))
            {
                return;
            }

            var state = new State(list);
            States.Add(list, state);
            state.Attach();
        }
        else if (States.TryGetValue(list, out var state))
        {
            state.Detach();
            States.Remove(list);
        }
    }

    /// <summary>Builds the entrance animation for one row. Internal so tests can inspect it.</summary>
    /// <param name="delay">The row's stagger delay.</param>
    internal static Animation BuildEntrance(TimeSpan delay) =>
        EntranceAnimation.Build(Motion.ListItemEnter, delay, Motion.ListItemOffset);

    /// <summary>
    /// True when the list's window is on screen: shown and not minimised. A list outside any window
    /// (a popup's own top level) counts as visible.
    /// </summary>
    private static bool IsOnScreen(Visual list) =>
        TopLevel.GetTopLevel(list) switch
        {
            null => false,
            Window w => w.IsVisible && w.WindowState != WindowState.Minimized,
            var topLevel => topLevel.IsVisible,
        };

    private static void Animate(Control container, TimeSpan delay)
    {
        Cancel(container);

        var cts = new CancellationTokenSource();
        Running.AddOrUpdate(container, cts);
        RunAsync(container, delay, cts).FireAndForget("ListEntrance");
    }

    private static async Task RunAsync(Control container, TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await BuildEntrance(delay).RunAsync(container, cts.Token);
        }
        finally
        {
            // Only the run that is still current cleans up; a recycled container's run was
            // cancelled and cleaned in Cancel, and a newer run owns the values now.
            if (Running.TryGetValue(container, out var current) && ReferenceEquals(current, cts))
            {
                Running.Remove(container);
                Clear(container);
            }

            cts.Dispose();
        }
    }

    private static void Cancel(Control container)
    {
        if (Running.TryGetValue(container, out var cts))
        {
            Running.Remove(container);
            cts.Cancel();
            Clear(container);
        }
    }

    private static void Clear(Control container) => EntranceAnimation.Clear(container);

    /// <summary>Per-list bookkeeping: the rows added since the last layout pass.</summary>
    private sealed class State(ItemsControl list)
    {
        private readonly Dictionary<object, int> _pending = new(ReferenceEqualityComparer.Instance);
        private int _batchSize;
        private bool _batchOpen;
        private bool _isReload;

        public void Attach()
        {
            list.Items.CollectionChanged += OnCollectionChanged;
            list.ContainerPrepared += OnContainerPrepared;
            list.ContainerClearing += OnContainerClearing;
        }

        public void Detach()
        {
            list.Items.CollectionChanged -= OnCollectionChanged;
            list.ContainerPrepared -= OnContainerPrepared;
            list.ContainerClearing -= OnContainerClearing;
            CloseBatch();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // A reload. Whatever is added in the same burst (the Clear-then-Add-each shape a
                // filter or refresh uses) is part of it, so the burst is marked and nothing in it
                // animates.
                OpenBatch();
                _pending.Clear();
                _isReload = true;
                return;
            }

            // Replace, Move, Remove: nothing about them is a row arriving.
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null)
            {
                return;
            }

            // Not on screen or motion reduced: do not even start a batch.
            if (Motion.IsReducedMotion || !list.IsVisible || !IsOnScreen(list))
            {
                return;
            }

            OpenBatch();
            if (_isReload)
            {
                return;
            }

            foreach (var item in e.NewItems)
            {
                if (item is not null)
                {
                    _pending[item] = _batchSize;
                }

                _batchSize++;
            }
        }

        private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var item = list.ItemFromContainer(e.Container);
            if (item is null || !_pending.Remove(item, out var order))
            {
                return;
            }

            if (!Motion.ShouldAnimateBatch(_batchSize, Motion.IsReducedMotion, IsOnScreen(list)))
            {
                return;
            }

            Animate(e.Container, Motion.StaggerDelay(order, reducedMotion: false));
        }

        private void OnContainerClearing(object? sender, ContainerClearingEventArgs e) => Cancel(e.Container);

        private void OnLayoutUpdated(object? sender, EventArgs e) => CloseBatch();

        private void OpenBatch()
        {
            if (_batchOpen)
            {
                return;
            }

            _batchOpen = true;
            // Containers for this burst are prepared during the next layout pass; once that pass is
            // over, anything still pending was never realized (scrolled out of a virtualized
            // viewport) and must not animate when it eventually is.
            list.LayoutUpdated += OnLayoutUpdated;
        }

        private void CloseBatch()
        {
            if (_batchOpen)
            {
                list.LayoutUpdated -= OnLayoutUpdated;
            }

            _batchOpen = false;
            _isReload = false;
            _batchSize = 0;
            _pending.Clear();
        }
    }
}
