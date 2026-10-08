using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.Logging;
using ReactiveUI;

namespace NexusMods.App.UI.WorkspaceSystem;

[PseudoClasses(":one-tab", ":selected", ":alone")]
public partial class PanelView : ReactiveUserControl<IPanelViewModel>
{
    private const double ScrollOffset = 250;
    internal const double DefaultPadding = 6.0;

    /// <summary>
    /// How far the pointer has to travel before a press on a tab header counts as a drag
    /// rather than a click.
    /// </summary>
    private const double TabDragThreshold = 6.0;

    /// <summary>
    /// How fast a tab slides into a new place, in pixels per millisecond, bounded below and
    /// above. Timing a slide by distance rather than giving every one the same duration is
    /// what keeps tabs moving at a consistent rate: a tab retargeted part way through its
    /// travel has less distance left, and covering it in a full-length animation is what
    /// makes neighbours appear to drift apart.
    /// </summary>
    private const double TabSlideSpeed = 1.4;

    private static readonly TimeSpan TabSlideMinDuration = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TabSlideMaxDuration = TimeSpan.FromMilliseconds(130);

    private PanelTabId? _pressedTabId;
    private double _pressOriginX;
    private double _dragGrabOffsetX;
    private bool _isReorderingTab;
    private int _dragTargetIndex = -1;
    private double _dragVisualLeft;
    private CancellationTokenSource? _dropAnimation;
    private bool _finishingDrop;

    /// <summary>
    /// The slide currently running on each tab, so it can be stopped before a new one takes
    /// its place. Avalonia does not replace an animation when a second one starts on the
    /// same property: both keep writing, and the longer-running one takes over again when
    /// the other ends.
    /// </summary>
    private readonly Dictionary<Control, CancellationTokenSource> _slideAnimations = new();

    public PanelView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.WhenAnyValue(view => view.ViewModel!.LogicalBounds)
                .Do(logicalBounds =>
                {
                    var left = logicalBounds.Left.IsCloseTo(0.0) ? 0.0 : DefaultPadding;
                    var top = logicalBounds.Top.IsCloseTo(0.0) ? 0.0 : DefaultPadding;
                    var right = logicalBounds.Right.IsCloseTo(1.0) ? 0.0 : DefaultPadding;
                    var bottom = logicalBounds.Bottom.IsCloseTo(1.0) ? 0.0 : DefaultPadding;

                    Padding = new Thickness(left, top, right, bottom);
                })
                .Subscribe()
                .DisposeWith(disposables);

            // panel selection
            this.AddDisposableHandler(PointerPressedEvent, (_, _) =>
            {
                if (ViewModel is not null) ViewModel.IsSelected = true;
            }, routes: RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true).DisposeWith(disposables);

            // drag a tab header sideways to reorder it. handledEventsToo is needed because
            // the header itself handles the press to select the tab.
            TabHeaders.AddDisposableHandler(PointerPressedEvent, OnTabHeaderPointerPressed,
                routes: RoutingStrategies.Bubble, handledEventsToo: true).DisposeWith(disposables);

            TabHeaders.AddDisposableHandler(PointerMovedEvent, OnTabHeaderPointerMoved,
                routes: RoutingStrategies.Bubble, handledEventsToo: true).DisposeWith(disposables);

            TabHeaders.AddDisposableHandler(PointerReleasedEvent, OnTabHeaderPointerReleased,
                routes: RoutingStrategies.Bubble, handledEventsToo: true).DisposeWith(disposables);

            TabHeaders.AddDisposableHandler(PointerCaptureLostEvent, OnTabHeaderPointerCaptureLost,
                routes: RoutingStrategies.Bubble, handledEventsToo: true).DisposeWith(disposables);

            this.WhenAnyValue(view => view.IsKeyboardFocusWithin)
                .Where(isFocused => isFocused)
                .SubscribeWithErrorLogging(_ =>
                {
                    if (ViewModel is not null) ViewModel.IsSelected = true;
                }).DisposeWith(disposables);

            // update scroll buttons and AddTab button (show left aligned or right aligned, depending on the scrollbar visibility)
            Observable.FromEventPattern<ScrollChangedEventArgs>(
                    addHandler => TabHeaderScrollViewer.ScrollChanged += addHandler,
                    removeHandler => TabHeaderScrollViewer.ScrollChanged -= removeHandler
                ).Select(_ => TabHeaderScrollViewer.Extent.Width > TabHeaderScrollViewer.Viewport.Width)
                .SubscribeWithErrorLogging(isScrollbarVisible =>
                {
                    ScrollLeftButton.IsVisible = isScrollbarVisible;
                    ScrollRightButton.IsVisible = isScrollbarVisible;

                    // the first button is inside the scroll area
                    AddTabButton1Container.IsVisible = !isScrollbarVisible;

                    // the second button is fixed on the right side
                    AddTabButton2.IsVisible = isScrollbarVisible;

                    var scrollBarMaximum = TabHeaderScrollViewer.ScrollBarMaximum;
                    var offset = TabHeaderScrollViewer.Offset;

                    ScrollLeftButton.IsEnabled = offset.X > 0;
                    ScrollRightButton.IsEnabled = !offset.X.IsCloseTo(scrollBarMaximum.X);
                })
                .DisposeWith(disposables);

            // set the bounds
            this.WhenAnyValue(view => view.ViewModel!.ActualBounds)
                .SubscribeWithErrorLogging(bounds =>
                {
                    Width = bounds.Width;
                    Height = bounds.Height;
                    Canvas.SetLeft(this, bounds.X);
                    Canvas.SetTop(this, bounds.Y);
                })
                .DisposeWith(disposables);

            // close panel button
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ClosePanelButton)
                .DisposeWith(disposables);
            
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ClosePanelButton2)
                .DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.CloseCommand.CanExecute)
                .BindToView(this, view => view.ClosePanelButton.IsVisible)
                .DisposeWith(disposables);
            
            this.WhenAnyObservable(view => view.ViewModel!.CloseCommand.CanExecute)
                .BindToView(this, view => view.FloatingClosePanelBorder.IsVisible)
                .DisposeWith(disposables);

            // popout panel button
            this.BindCommand(ViewModel, vm => vm.PopoutCommand, view => view.PopOutPanelButton)
                .DisposeWith(disposables);

            this.WhenAnyObservable(view => view.ViewModel!.PopoutCommand.CanExecute)
                .BindToView(this, view => view.PopOutPanelButton.IsVisible)
                .DisposeWith(disposables);

            // two "add tab" buttons
            this.BindCommand(ViewModel, vm => vm.AddTabCommand, view => view.AddTabButton1)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.AddTabCommand, view => view.AddTabButton2)
                .DisposeWith(disposables);

            // tab contents and headers
            // Contents bind to the stable collection, so reordering the headers doesn't
            // rebuild the pages. Only the headers follow the user-visible order.
            this.OneWayBind(ViewModel, vm => vm.TabContents, view => view.TabContents.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Tabs, view => view.TabHeaders.ItemsSource)
                .DisposeWith(disposables);

            // button to scroll to the left
            Observable.FromEventPattern<RoutedEventArgs>(
                    addHandler => ScrollLeftButton.Click += addHandler,
                    removeHandler => ScrollLeftButton.Click -= removeHandler
                ).Select(_ =>
                {
                    var currentOffset = TabHeaderScrollViewer.Offset;
                    return currentOffset.WithX(currentOffset.X - ScrollOffset);
                })
                .BindToView(this, view => view.TabHeaderScrollViewer.Offset)
                .DisposeWith(disposables);

            // button to scroll to the right
            Observable.FromEventPattern<RoutedEventArgs>(
                    addHandler => ScrollRightButton.Click += addHandler,
                    removeHandler => ScrollRightButton.Click -= removeHandler
                ).Select(_ =>
                {
                    var currentOffset = TabHeaderScrollViewer.Offset;
                    return currentOffset.WithX(currentOffset.X + ScrollOffset);
                })
                .BindToView(this, view => view.TabHeaderScrollViewer.Offset)
                .DisposeWith(disposables);
            
            this.WhenAnyValue(view => view.ViewModel!.Tabs.Count,
                view => view.ViewModel!.IsAlone)
                .Select(tuple =>
                    {
                        // we need a floating tab close button if there is only one tab and the panel is not alone
                        var (tabCount, isAlone) = tuple;
                        return tabCount == 1 && !isAlone;
                    }
                )
                .SubscribeWithErrorLogging(showFloatingClose =>
                    {
                        FloatingClosePanelBorder.IsVisible = showFloatingClose;
                    }
                )
                .DisposeWith(disposables);

            // pseudo classes
            this.WhenAnyValue(view => view.ViewModel!.Tabs.Count)
                .Select(count => count == 1)
                .SubscribeWithErrorLogging(hasOneTab =>
                    {
                        TabHeaderBorder.IsVisible = !hasOneTab;
                        PseudoClasses.Set(":one-tab", hasOneTab);
                    }
                )
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.IsSelected)
                .SubscribeWithErrorLogging(isSelected => PseudoClasses.Set(":selected", isSelected))
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.IsAlone)
                .SubscribeWithErrorLogging(isAlone =>
                    {
                        PseudoClasses.Set(":alone", isAlone);
                    }
                )
                .DisposeWith(disposables);
        });
    }

    // The three handlers below run inside Avalonia's input dispatch, where an escaping
    // exception takes the whole application down rather than surfacing as an error. Tab
    // reordering is cosmetic, so each one fails safe: losing the gesture beats losing the
    // session. This mirrors how the in-app browser guards its Chromium callbacks.
    private void OnTabHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            HandleTabHeaderPointerPressed(e);
        }
        catch (Exception exception)
        {
            AbandonTabDrag(exception);
        }
    }

    private void OnTabHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            HandleTabHeaderPointerMoved(e);
        }
        catch (Exception exception)
        {
            AbandonTabDrag(exception);
        }
    }

    private void OnTabHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            HandleTabHeaderPointerReleased(e);
        }
        catch (Exception exception)
        {
            AbandonTabDrag(exception);
        }
    }

    private void OnTabHeaderPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_finishingDrop) return;

        try
        {
            ResetTabDrag();
        }
        catch (Exception exception)
        {
            ReactiveUiExtensions.DefaultLogger.LogError(exception, "Unable to reset the tab drag");
        }
    }

    private void AbandonTabDrag(Exception exception)
    {
        // Logged rather than written to Debug: this path swallowing an exception silently
        // is what made a broken slide animation look like a drag that kept losing focus.
        ReactiveUiExtensions.DefaultLogger.LogError(exception, "Tab drag failed and was abandoned");

        try
        {
            ResetTabDrag();
        }
        catch (Exception nested)
        {
            // Nothing useful is left to do, and throwing from here would defeat the point.
            ReactiveUiExtensions.DefaultLogger.LogError(nested, "Unable to reset the tab drag");
        }
    }

    private void HandleTabHeaderPointerPressed(PointerPressedEventArgs e)
    {
        ResetTabDrag();

        if (ViewModel is null) return;
        if (!e.GetCurrentPoint(TabHeaders).Properties.IsLeftButtonPressed) return;

        // A press that lands on the close button isn't the start of a reorder.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;

        var panel = TabHeaders.ItemsPanelRoot;
        if (panel is null) return;

        var container = GetTabContainerUnder(e);
        if (container is null) return;
        if (TabHeaders.ItemFromContainer(container) is not IPanelTabViewModel tab) return;

        var pointerX = e.GetPosition(panel).X;

        _pressedTabId = tab.Id;
        _pressOriginX = pointerX;
        _dragTargetIndex = IndexOfTab(tab.Id);

        // Where along the tab it was grabbed, so dragging picks it up at that point
        // instead of snapping its left edge to the cursor.
        _dragGrabOffsetX = pointerX - container.Bounds.X;
    }

    private void HandleTabHeaderPointerMoved(PointerEventArgs e)
    {
        if (_pressedTabId is null || ViewModel is null) return;

        var panel = TabHeaders.ItemsPanelRoot;
        if (panel is null) return;

        // Everything below works in the items panel's coordinates: container Bounds are
        // layout values in that same space, so they stay comparable with the pointer no
        // matter what render transform a tab is carrying.
        var pointerX = e.GetPosition(panel).X;

        if (!_isReorderingTab)
        {
            // Nothing is captured yet, so a release outside the strip never reaches us and
            // the button state is the only thing that can clear a stale press.
            if (!e.GetCurrentPoint(TabHeaders).Properties.IsLeftButtonPressed)
            {
                ResetTabDrag();
                return;
            }

            if (Math.Abs(pointerX - _pressOriginX) < TabDragThreshold) return;

            _isReorderingTab = true;

            // Captured only once the drag is real, so an ordinary click and a middle-click
            // close still reach the tab header itself. This is what keeps the tab following
            // the cursor when it strays outside the strip.
            //
            // The button state is deliberately not re-checked past this point: with the
            // capture in place the release always comes back to us, and a move that reports
            // no buttons -- which is what arrives once the pointer is pinned against the
            // end of the strip or leaves the window -- would otherwise abandon the drag
            // halfway through.
            e.Pointer.Capture(TabHeaders);
        }

        var draggedIndex = IndexOfTab(_pressedTabId.Value);
        if (draggedIndex < 0) return;

        var dragged = TabHeaders.ContainerFromIndex(draggedIndex);
        if (dragged is null) return;

        var draggedWidth = dragged.Bounds.Width;

        // Where the tab is actually drawn. The ordering is judged on this rather than on
        // the cursor, so a wide tab pushes its neighbours aside as its body reaches them
        // instead of waiting for the pointer to get there.
        _dragVisualLeft = ClampVisualLeft(panel, draggedWidth, pointerX);

        var targetIndex = ComputeTargetIndex(draggedIndex, _dragVisualLeft);
        if (targetIndex != _dragTargetIndex)
        {
            _dragTargetIndex = targetIndex;
            ApplyDisplacement(draggedIndex, targetIndex, draggedWidth);
        }

        FollowPointer(dragged, _dragVisualLeft);
    }

    private void HandleTabHeaderPointerReleased(PointerReleasedEventArgs e)
    {
        if (!_isReorderingTab)
        {
            ResetTabDrag();
            return;
        }

        var tabId = _pressedTabId;
        var targetIndex = _dragTargetIndex;
        var visualLeft = _dragVisualLeft;

        _pressedTabId = null;
        _isReorderingTab = false;

        // Releasing the capture raises PointerCaptureLost synchronously, and the handler
        // for it wipes every drag transform. That would undo the arrangement this drop is
        // about to commit, hence the flag.
        _finishingDrop = true;
        try
        {
            e.Pointer.Capture(null);
        }
        finally
        {
            _finishingDrop = false;
        }

        if (tabId is null || ViewModel is null)
        {
            ResetTabDrag();
            return;
        }

        CommitDrag(tabId.Value, targetIndex, visualLeft);
    }

    /// <summary>
    /// Writes the order the drag was showing into the panel and lands the dragged tab.
    /// </summary>
    /// <remarks>
    /// This is the only point at which the tab collection is touched. Reordering it on
    /// every pointer move instead makes the ItemsControl rebuild containers each time, and
    /// a rebuilt container is drawn for a frame before it has settled, which is visible as
    /// the row flickering.
    /// </remarks>
    private void CommitDrag(PanelTabId id, int targetIndex, double visualLeft)
    {
        CancelDropAnimation();

        var draggedIndex = IndexOfTab(id);

        // Dropping the transforms and committing the order belong together: the displaced
        // tabs are holding positions that only make sense against the old order, and the
        // reorder is what puts them there for real. Doing both before returning means no
        // frame is ever drawn with one applied and not the other.
        ClearAllDragVisuals();

        if (draggedIndex >= 0 && targetIndex >= 0 && targetIndex != draggedIndex)
        {
            ViewModel!.MoveTab(id, targetIndex);
        }

        var panel = TabHeaders.ItemsPanelRoot;
        panel?.UpdateLayout();

        var index = IndexOfTab(id);
        if (index < 0) return;

        var container = TabHeaders.ContainerFromIndex(index);
        if (container is null) return;

        // Land it from wherever the cursor left it into the slot it now occupies.
        SlideDroppedTabHome(container, visualLeft - container.Bounds.X);
    }

    /// <summary>
    /// Offsets the tabs the dragged one has stepped over, so the row looks reordered while
    /// the collection itself is untouched.
    /// </summary>
    /// <remarks>
    /// A tab either sits still or is pushed aside by exactly the dragged tab's width: left
    /// when the dragged tab has moved right past it, right when it has moved left past it.
    /// Nothing in between, so a tab whose place has not changed is never given a value to
    /// animate to and cannot drift.
    /// </remarks>
    private void ApplyDisplacement(int draggedIndex, int targetIndex, double draggedWidth)
    {
        for (var i = 0; i < TabHeaders.ItemCount; i++)
        {
            if (i == draggedIndex) continue;

            var container = TabHeaders.ContainerFromIndex(i);
            if (container is null) continue;

            var desired =
                targetIndex > draggedIndex && i > draggedIndex && i <= targetIndex ? -draggedWidth :
                targetIndex < draggedIndex && i >= targetIndex && i < draggedIndex ? draggedWidth :
                0.0;

            // X holds the resting value this tab was last sent to, which is what says
            // whether its place in the row has actually changed.
            var resting = (container.RenderTransform as TranslateTransform)?.X ?? 0.0;
            if (Math.Abs(resting - desired) < 0.5) continue;

            StartSlideTo(container, desired);
        }
    }

    /// <summary>
    /// Holds the dragged tab so that its left edge sits at <paramref name="visualLeft"/>.
    /// </summary>
    private static void FollowPointer(Control dragged, double visualLeft)
    {
        // An offset from where layout put the tab, which is why it is recomputed rather
        // than accumulated. No animation: this one tracks the cursor.
        EnsureTranslateTransform(dragged).X = visualLeft - dragged.Bounds.X;
        dragged.ZIndex = 1;
    }

    /// <summary>
    /// Where the dragged tab wants to be drawn: under the cursor, held inside the strip.
    /// </summary>
    private double ClampVisualLeft(Panel panel, double draggedWidth, double pointerX)
    {
        var maxLeft = Math.Max(0.0, panel.Bounds.Width - draggedWidth);
        return Math.Clamp(pointerX - _dragGrabOffsetX, 0.0, maxLeft);
    }

    /// <summary>
    /// Settles the dropped tab into its slot instead of letting it snap there.
    /// </summary>
    private void SlideDroppedTabHome(Control container, double offset)
    {
        if (Math.Abs(offset) < 0.5)
        {
            ClearDragVisual(container);
            return;
        }

        // Stays lifted above its neighbours until it has landed.
        container.ZIndex = 1;

        // The dragged tab is never given a slide of its own, but make sure of it: a stray
        // one would fight the landing exactly as two slides fight each other.
        CancelSlide(container);

        var cancellation = new CancellationTokenSource();
        _dropAnimation = cancellation;

        _ = FinishDropAsync(container, offset, cancellation);
    }

    private async Task FinishDropAsync(Control container, double offset, CancellationTokenSource cancellation)
    {
        try
        {
            await StartSlide(container, offset, cancellation.Token);
        }
        catch (Exception e)
        {
            ReactiveUiExtensions.DefaultLogger.LogWarning(e, "Tab drop animation failed");
        }

        // Cancelling the animation completes the task normally rather than throwing, so
        // the token is what says whether this drop still owns the tab. Without the check a
        // drag that started in the meantime would have its offset wiped.
        if (cancellation.IsCancellationRequested) return;

        ClearDragVisual(container);

        if (!ReferenceEquals(_dropAnimation, cancellation)) return;

        _dropAnimation = null;
        cancellation.Dispose();
    }

    /// <summary>
    /// Slides <paramref name="container"/> from wherever it is currently drawn to
    /// <paramref name="toX"/>.
    /// </summary>
    private void StartSlideTo(Control container, double toX)
    {
        var transform = EnsureTranslateTransform(container);

        // Read before anything else: this is where the tab is drawn right now, which may be
        // part way through an earlier slide, so reversing direction mid-flight picks up from
        // there instead of jumping.
        var fromX = transform.Value.M31;

        // Stopping the old slide and starting the new one happen together, without yielding,
        // so the frame in which the old animation has been dropped and the new one not yet
        // applied is never drawn. Cancelling alone would snap the tab to the resting value
        // the old slide was heading for.
        CancelSlide(container);

        transform.X = toX;

        var cancellation = new CancellationTokenSource();
        _slideAnimations[container] = cancellation;

        _ = TrackSlideAsync(container, fromX, toX, cancellation);
    }

    private async Task TrackSlideAsync(Control container, double fromX, double toX, CancellationTokenSource cancellation)
    {
        try
        {
            await RunSlide(container, fromX, toX, cancellation.Token);
        }
        catch (Exception e)
        {
            ReactiveUiExtensions.DefaultLogger.LogWarning(e, "Tab slide animation failed");
        }

        // Only tidy up if this slide is still the current one for the tab.
        if (!_slideAnimations.TryGetValue(container, out var current)) return;
        if (!ReferenceEquals(current, cancellation)) return;

        _slideAnimations.Remove(container);
        cancellation.Dispose();
    }

    private void CancelSlide(Control container)
    {
        if (!_slideAnimations.Remove(container, out var cancellation)) return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void CancelAllSlides()
    {
        foreach (var cancellation in _slideAnimations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _slideAnimations.Clear();
    }

    /// <summary>
    /// Animates <paramref name="container"/> from <paramref name="offset"/> back to its
    /// laid-out position, which reads as the tab travelling into place.
    /// </summary>
    private static Task StartSlide(Control container, double offset, CancellationToken cancellationToken)
    {
        EnsureTranslateTransform(container).X = 0.0;
        return RunSlide(container, offset, 0.0, cancellationToken);
    }

    /// <summary>
    /// How long a tab should take to cover <paramref name="distance"/>.
    /// </summary>
    private static TimeSpan SlideDuration(double distance)
    {
        var milliseconds = Math.Abs(distance) / TabSlideSpeed;

        return TimeSpan.FromMilliseconds(Math.Clamp(
            milliseconds,
            TabSlideMinDuration.TotalMilliseconds,
            TabSlideMaxDuration.TotalMilliseconds));
    }

    /// <remarks>
    /// Two things about the target are easy to get wrong, and both fail only at runtime.
    /// The animated property is <see cref="TranslateTransform.XProperty"/>, but the target
    /// handed to <c>RunAsync</c> is the container, not its transform: Avalonia animates
    /// that property through an animator which reaches the transform via the visual's
    /// <see cref="Visual.RenderTransform"/>, and passing the transform itself throws an
    /// <see cref="InvalidCastException"/>. Animating <see cref="Visual.RenderTransform"/>
    /// directly is not an alternative either, because no animator is registered for that
    /// property and <c>RunAsync</c> throws an <see cref="InvalidOperationException"/>
    /// saying so.
    ///
    /// Callers assign the resting value themselves and the animation is left on the default
    /// fill mode, so once it finishes the property falls back to that rather than staying
    /// pinned at animation priority, which would override the next drag of this tab.
    /// </remarks>
    private static Task RunSlide(Control container, double fromX, double toX, CancellationToken cancellationToken)
    {
        var animation = new Animation
        {
            Duration = SlideDuration(toX - fromX),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters = { new Setter(TranslateTransform.XProperty, fromX) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters = { new Setter(TranslateTransform.XProperty, toX) },
                },
            },
        };

        return animation.RunAsync(container, cancellationToken);
    }

    private static TranslateTransform EnsureTranslateTransform(Control container)
    {
        if (container.RenderTransform is TranslateTransform existing) return existing;

        var transform = new TranslateTransform();
        container.RenderTransform = transform;
        return transform;
    }

    /// <summary>
    /// Where the dragged tab belongs, given that its left edge is at
    /// <paramref name="visualLeft"/>.
    /// </summary>
    /// <remarks>
    /// The other tabs are walked in order as if the dragged one had been lifted out of the
    /// row, counting how many of their midpoints its leading edge has passed. Landing on
    /// the nearest gap this way is what keeps the dragged tab covering as little of its
    /// neighbours as the geometry allows, so a wide tab displaces narrow ones as soon as it
    /// reaches them rather than sitting on top of them.
    ///
    /// The answer depends only on <paramref name="visualLeft"/> and on the other tabs'
    /// widths. Neither changes while a drag is in progress, now that the collection is left
    /// alone until the drop, so the result cannot feed back into itself and the order
    /// cannot oscillate.
    /// </remarks>
    private int ComputeTargetIndex(int draggedIndex, double visualLeft)
    {
        var target = 0;
        var accumulated = 0.0;

        for (var i = 0; i < TabHeaders.ItemCount; i++)
        {
            if (i == draggedIndex) continue;

            var container = TabHeaders.ContainerFromIndex(i);
            if (container is null) continue;

            var width = container.Bounds.Width;
            if (visualLeft <= accumulated + width / 2.0) break;

            accumulated += width;
            target++;
        }

        return target;
    }

    private int IndexOfTab(PanelTabId id)
    {
        if (ViewModel is null) return -1;

        var tabs = ViewModel.Tabs;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].Id == id) return i;
        }

        return -1;
    }

    private void ResetTabDrag()
    {
        CancelDropAnimation();

        _pressedTabId = null;
        _isReorderingTab = false;
        _dragTargetIndex = -1;
        _dragVisualLeft = 0.0;

        ClearAllDragVisuals();
    }

    private void ClearAllDragVisuals()
    {
        // Stopped first: an animation left running would carry on writing to a transform
        // that has just been taken away.
        CancelAllSlides();

        if (TabHeaders.ItemsPanelRoot is null) return;
        foreach (var container in TabHeaders.GetRealizedContainers()) ClearDragVisual(container);
    }

    private void CancelDropAnimation()
    {
        var cancellation = _dropAnimation;
        _dropAnimation = null;
        if (cancellation is null) return;

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static void ClearDragVisual(Control container)
    {
        container.RenderTransform = null;
        container.ZIndex = 0;
    }

    /// <summary>
    /// The realized tab header the pointer is horizontally over, if any. Only used to pick
    /// the tab at the start of a drag, before any transform is in play.
    /// </summary>
    private Control? GetTabContainerUnder(PointerEventArgs e)
    {
        foreach (var container in TabHeaders.GetRealizedContainers())
        {
            var local = e.GetPosition(container);
            if (local.X < 0 || local.X > container.Bounds.Width) continue;

            return container;
        }

        return null;
    }
}
