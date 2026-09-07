using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LoopIt7.ViewModels;

namespace LoopIt7.Views;

/// <summary>
/// The canvas: boxes you drag, ports you pull cables from, cables you click to trim.
/// <para>
/// All of it is mouse work against view models, so the drawing stays declarative in XAML and
/// this file only translates gestures into graph changes.
/// </para>
/// </summary>
public partial class PatchbayView : UserControl
{
    /// <summary>Boxes land on a grid. Tidy without anyone having to be careful.</summary>
    private const double SnapStep = 13;

    private MainViewModel? _viewModel;

    private PatchNodeViewModel? _draggingNode;
    private Point _dragGrabOffset;

    private SourceNodeViewModel? _cableOrigin;
    private readonly PathFigure _dragFigure = new();
    private readonly BezierSegment _dragCurve = new() { IsStroked = true };

    public PatchbayView()
    {
        InitializeComponent();

        _dragFigure.Segments.Add(_dragCurve);
        DragWire.Data = new PathGeometry([_dragFigure]);

        DataContextChanged += (_, e) => _viewModel = e.NewValue as MainViewModel;
    }

    // Moving a box

    private void OnNodeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PatchNodeViewModel node }) return;

        _draggingNode = node;

        Point position = e.GetPosition(Surface);
        _dragGrabOffset = new Point(position.X - node.X, position.Y - node.Y);

        if (_viewModel is not null) _viewModel.SelectedCable = null;
        Surface.CaptureMouse();
        e.Handled = true;
    }

    // Pulling a cable

    private void OnPortMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SourceNodeViewModel source }) return;

        _cableOrigin = source;
        UpdateDragWire(e.GetPosition(Surface));
        DragWire.Visibility = Visibility.Visible;

        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        Point position = e.GetPosition(Surface);

        if (_draggingNode is not null)
        {
            double x = Math.Max(0, position.X - _dragGrabOffset.X);
            double y = Math.Max(0, position.Y - _dragGrabOffset.Y);

            double snappedX = Math.Round(x / SnapStep) * SnapStep;
            double snappedY = Math.Round(y / SnapStep) * SnapStep;

            if (Math.Abs(snappedX - _draggingNode.X) > 0.01 || Math.Abs(snappedY - _draggingNode.Y) > 0.01)
            {
                _draggingNode.X = snappedX;
                _draggingNode.Y = snappedY;
            }

            return;
        }

        if (_cableOrigin is not null) UpdateDragWire(position);
    }

    private void OnSurfaceMouseUp(object sender, MouseEventArgs e)
    {
        if (_cableOrigin is not null)
        {
            var target = FindDestinationUnder(e.GetPosition(Surface));
            if (target is not null && _viewModel is not null)
            {
                if (!_viewModel.TryConnect(_cableOrigin, target))
                {
                    // Already patched. Say so rather than silently doing nothing.
                    ShowAlreadyPatched(_cableOrigin, target);
                }
            }

            _cableOrigin = null;
            DragWire.Visibility = Visibility.Collapsed;
        }

        _draggingNode = null;
        Surface.ReleaseMouseCapture();
    }

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click on bare canvas is how you put the cable inspector away.
        if (ReferenceEquals(e.OriginalSource, Surface) && _viewModel is not null)
        {
            _viewModel.SelectedCable = null;
        }
    }

    private void OnCableMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CableViewModel cable } || _viewModel is null) return;

        _viewModel.SelectedCable = ReferenceEquals(_viewModel.SelectedCable, cable) ? null : cable;
        e.Handled = true;
    }

    private void OnCableDelayDown(object sender, RoutedEventArgs e) => StepCableDelay(-1);

    private void OnCableDelayUp(object sender, RoutedEventArgs e) => StepCableDelay(+1);

    private void StepCableDelay(int direction)
    {
        var cable = _viewModel?.SelectedCable;
        if (cable is null) return;

        // Shift walks in tens, which is roughly three metres of air per press.
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        cable.DelayMs += direction * step;
    }

    private void UpdateDragWire(Point cursor)
    {
        if (_cableOrigin is null) return;

        double x1 = _cableOrigin.OutputPortX;
        double y1 = _cableOrigin.PortY;
        double reach = Math.Clamp(Math.Abs(cursor.X - x1) * 0.5, 60, 190);

        _dragFigure.StartPoint = new Point(x1, y1);
        _dragCurve.Point1 = new Point(x1 + reach, y1);
        _dragCurve.Point2 = new Point(cursor.X - reach, cursor.Y);
        _dragCurve.Point3 = cursor;
    }

    /// <summary>
    /// Finds the destination box under the cursor. Hit testing the visual tree would land on
    /// whichever child happens to be on top, so this walks the boxes by their own geometry.
    /// </summary>
    private DestinationNodeViewModel? FindDestinationUnder(Point position)
    {
        if (_viewModel is null) return null;

        foreach (var destination in _viewModel.Destinations)
        {
            // A generous margin on the left, so aiming at the port is enough.
            var bounds = new Rect(
                destination.X - 16,
                destination.Y,
                PatchNodeViewModel.NodeWidth + 16,
                PatchNodeViewModel.NodeHeight);

            if (bounds.Contains(position)) return destination;
        }

        return null;
    }

    private void ShowAlreadyPatched(SourceNodeViewModel source, DestinationNodeViewModel destination)
    {
        var existing = _viewModel?.Cables.FirstOrDefault(c => c.Source == source && c.Destination == destination);
        if (existing is not null && _viewModel is not null) _viewModel.SelectedCable = existing;
    }
}
