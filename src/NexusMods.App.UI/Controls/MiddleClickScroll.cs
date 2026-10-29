using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Path = Avalonia.Controls.Shapes.Path;

namespace NexusMods.App.UI.Controls;

/// <summary>
/// Scrolling with the middle mouse button, the way web browsers do it on Windows.
/// </summary>
/// <remarks>
/// A middle press over anything that scrolls leaves a marker there, and the content then
/// scrolls towards the pointer, the faster the further the pointer is from the marker.
/// Released after a drag, the button ends it; released without moving, it leaves the
/// scrolling on until the next click, key press or turn of the wheel.
///
/// A page in the in-app browser is a native window whose input never reaches the app, so
/// web pages get the same from Chromium itself, which Startup turns on.
/// </remarks>
public static class MiddleClickScroll
{
    /// <summary>
    /// False on an element that puts the middle button to a use of its own, such as a tab
    /// header closing its tab, so that a press there is left to it.
    /// </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(MiddleClickScroll), defaultValue: true);

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    // How far the pointer can stray from the marker along either axis before that axis
    // starts to scroll, and before a press with the button held counts as a drag.
    private const double DeadZone = 15;

    private static Session? _session;

    // The click that ends a session belongs to the session, release included.
    private static bool _swallowRelease;

    /// <summary>
    /// Hooks every window in the app. Call once at startup.
    /// </summary>
    public static void Register()
    {
        // Tunnel, so the press is taken before anything under the pointer acts on it: a grid
        // row would select itself and mark the press handled.
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>(OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>(OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerMovedEvent.AddClassHandler<TopLevel>(OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>(static (_, _) => Stop(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>(OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static void OnPointerPressed(TopLevel topLevel, PointerPressedEventArgs e)
    {
        if (_session is not null)
        {
            Stop();
            e.Handled = true;
            _swallowRelease = true;
            return;
        }

        // A release that never came (it happened outside the app) mustn't take this click's.
        _swallowRelease = false;

        if (e.Handled || e.Pointer.Type != PointerType.Mouse) return;

        var point = e.GetCurrentPoint(topLevel);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed) return;
        if (e.Source is not Visual source || FindScrollTarget(source) is not { } target) return;

        e.Handled = true;
        _session = new Session(topLevel, target, e.Pointer, point.Position);

        // The window keeps the pointer until the session ends, so nothing lights up as the
        // pointer passes over it and the cursor stays the scrolling one.
        e.Pointer.Capture(topLevel);
    }

    private static void OnPointerReleased(TopLevel topLevel, PointerReleasedEventArgs e)
    {
        if (_swallowRelease)
        {
            _swallowRelease = false;
            e.Handled = true;
            return;
        }

        if (_session is not { } session || e.InitialPressMouseButton != MouseButton.Middle) return;
        e.Handled = true;

        if (session.Dragged)
        {
            Stop();
            return;
        }

        // Avalonia lets go of the pointer once a release is through, so take it back after.
        var pointer = e.Pointer;
        Dispatcher.UIThread.Post(() =>
        {
            if (_session == session) pointer.Capture(topLevel);
        });
    }

    private static void OnPointerMoved(TopLevel topLevel, PointerEventArgs e)
    {
        if (_session is { } session && session.TopLevel == topLevel)
            session.MoveTo(e.GetPosition(topLevel));
    }

    private static void OnKeyDown(TopLevel topLevel, KeyEventArgs e)
    {
        if (_session is null) return;
        Stop();

        // Escape only ends the session; any other key goes on to do what it does.
        if (e.Key == Key.Escape) e.Handled = true;
    }

    private static void Stop()
    {
        if (_session is not { } session) return;
        _session = null;
        session.Dispose();
    }

    /// <summary>
    /// The nearest scroller around <paramref name="source"/> with anything to scroll, or
    /// null if the press belongs to the element under the pointer.
    /// </summary>
    private static ScrollViewer? FindScrollTarget(Visual source)
    {
        for (var visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            // Buttons take the middle button too: a NavigationControl opens its page in a new
            // tab with it. Web browsers likewise leave a middle click on a link to the link.
            if (visual is Button) return null;
            if (visual is Control control && !GetIsEnabled(control)) return null;
            if (visual is ScrollViewer scrollViewer && Overflow(scrollViewer) is { } overflow && (overflow.X >= 1 || overflow.Y >= 1))
                return scrollViewer;
        }

        return null;
    }

    /// <summary>
    /// How far the content of <paramref name="scrollViewer"/> runs past its viewport on each axis.
    /// </summary>
    private static Vector Overflow(ScrollViewer scrollViewer) => new(
        Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width),
        Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
    );

    private sealed class Session : IDisposable
    {
        public TopLevel TopLevel { get; }

        /// <summary>
        /// Whether the pointer has left the dead zone since the press.
        /// </summary>
        public bool Dragged { get; private set; }

        private readonly ScrollViewer _target;
        private readonly IPointer _pointer;
        private readonly Point _origin;
        private readonly IDisposable? _cursor;
        private readonly OverlayLayer? _markerLayer;
        private readonly Control? _marker;

        private Point _position;
        private TimeSpan? _lastFrame;

        // Kept here rather than read back every frame: near the marker the content moves by
        // less than a pixel a frame, which the scroller could round away.
        private Vector _offset;

        public Session(TopLevel topLevel, ScrollViewer target, IPointer pointer, Point origin)
        {
            TopLevel = topLevel;
            _target = target;
            _pointer = pointer;
            _origin = _position = origin;
            _offset = target.Offset;

            var overflow = Overflow(target);
            var horizontal = overflow.X >= 1;
            var vertical = overflow.Y >= 1;

            var cursor = new Cursor(horizontal && vertical
                ? StandardCursorType.SizeAll
                : vertical ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast
            );

            // Above local values and styles, and undone by disposing, so whatever cursor the
            // window had comes back untouched.
            _cursor = topLevel.SetValue(InputElement.CursorProperty, cursor, BindingPriority.Animation);

            _markerLayer = OverlayLayer.GetOverlayLayer(topLevel);
            if (_markerLayer is not null)
            {
                _marker = CreateMarker(horizontal, vertical);
                var position = topLevel.TranslatePoint(origin, _markerLayer) ?? origin;
                Canvas.SetLeft(_marker, position.X - _marker.Width / 2);
                Canvas.SetTop(_marker, position.Y - _marker.Height / 2);
                _markerLayer.Children.Add(_marker);
            }

            if (topLevel is WindowBase window) window.Deactivated += OnDeactivated;

            topLevel.RequestAnimationFrame(OnFrame);
        }

        public void MoveTo(Point position)
        {
            _position = position;
            if (Math.Abs(position.X - _origin.X) > DeadZone || Math.Abs(position.Y - _origin.Y) > DeadZone)
                Dragged = true;
        }

        private void OnFrame(TimeSpan time)
        {
            if (_session != this) return;

            // Gone from view, as when the tab it was in is switched away from.
            if (!_target.IsEffectivelyVisible || TopLevel.GetTopLevel(_target) != TopLevel)
            {
                Stop();
                return;
            }

            // Capped, so a stalled frame doesn't land as one big jump.
            var seconds = _lastFrame is { } last ? Math.Min((time - last).TotalSeconds, 0.1) : 0;
            _lastFrame = time;

            // Moved by something else, such as the content above it growing.
            var current = _target.Offset;
            if (Math.Abs(current.X - _offset.X) > 1 || Math.Abs(current.Y - _offset.Y) > 1)
                _offset = current;

            var overflow = Overflow(_target);
            _offset = new Vector(
                Math.Clamp(_offset.X + Speed(_position.X - _origin.X) * seconds, 0, overflow.X),
                Math.Clamp(_offset.Y + Speed(_position.Y - _origin.Y) * seconds, 0, overflow.Y)
            );
            _target.Offset = _offset;

            TopLevel.RequestAnimationFrame(OnFrame);
        }

        /// <summary>
        /// Pixels a second to scroll with the pointer <paramref name="distance"/> pixels past
        /// the marker along an axis.
        /// </summary>
        /// <remarks>
        /// Modelled on Chromium's curve for web pages, so the app and the pages in it scroll
        /// alike: slow enough near the marker to read along, and quickly faster past it.
        /// </remarks>
        private static double Speed(double distance)
        {
            if (Math.Abs(distance) <= DeadZone) return 0;
            return Math.Sign(distance) * Math.Pow(Math.Abs(distance), 2.2) * 0.008;
        }

        /// <summary>
        /// A dot with arrows for the axes that scroll, like the one web browsers show.
        /// </summary>
        private Control CreateMarker(bool horizontal, bool vertical)
        {
            const string dot = "M12,14 A2,2 0 1 1 16,14 A2,2 0 1 1 12,14 Z";
            const string upDown = "M14,5 L18,10 L10,10 Z M14,23 L18,18 L10,18 Z";
            const string leftRight = "M5,14 L10,10 L10,18 Z M23,14 L18,10 L18,18 Z";

            var data = dot;
            if (vertical) data += " " + upDown;
            if (horizontal) data += " " + leftRight;

            return new Panel
            {
                Width = 28,
                Height = 28,
                IsHitTestVisible = false,
                Children =
                {
                    new Ellipse
                    {
                        Fill = FindBrush("SurfaceHighBrush"),
                        Stroke = FindBrush("StrokeTranslucentModerateBrush"),
                        StrokeThickness = 1,
                    },
                    new Path
                    {
                        Data = Geometry.Parse(data),
                        Fill = FindBrush("NeutralStrongBrush"),
                    },
                },
            };
        }

        private IBrush? FindBrush(string key)
        {
            return TopLevel.TryFindResource(key, TopLevel.ActualThemeVariant, out var value) ? value as IBrush : null;
        }

        private void OnDeactivated(object? sender, EventArgs e) => Stop();

        public void Dispose()
        {
            if (TopLevel is WindowBase window) window.Deactivated -= OnDeactivated;

            _cursor?.Dispose();
            if (_marker is not null) _markerLayer?.Children.Remove(_marker);
            if (_pointer.Captured == TopLevel) _pointer.Capture(null);
        }
    }
}
