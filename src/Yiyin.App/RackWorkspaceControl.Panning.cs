using System.Windows;
using System.Windows.Input;

namespace LightDeck.App;

public sealed partial class RackWorkspaceControl
{
    private double? _mixerPanStart;
    private double _mixerPanOffset;
    private Cursor? _mixerPanCursor;

    private void EnableMixerPanning()
    {
        _mixer.PreviewMouseDown += MixerPanDown;
        _mixer.PreviewMouseMove += MixerPanMove;
        _mixer.PreviewMouseUp += MixerPanUp;
        _mixer.LostMouseCapture += MixerPanLostCapture;
        _mixer.LostKeyboardFocus += MixerPanLostFocus;
        _owner.Deactivated += MixerPanDeactivated;
    }
    private void MixerPanDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _disposed || _mixer.ScrollableWidth <= 0) return;
        _mixer.Focus();
        if (!_mixer.CaptureMouse()) return;
        _mixerPanStart = e.GetPosition(_mixer).X; _mixerPanOffset = _mixer.HorizontalOffset;
        _mixerPanCursor = _mixer.Cursor; _mixer.Cursor = Cursors.ScrollWE; e.Handled = true;
    }
    private void MixerPanMove(object sender, MouseEventArgs e)
    {
        if (_mixerPanStart is null) return;
        ContinueMixerPan(e.GetPosition(_mixer).X); e.Handled = true;
    }
    private void ContinueMixerPan(double pointerX)
    {
        if (_mixerPanStart is { } start) _mixer.ScrollToHorizontalOffset(Math.Clamp(_mixerPanOffset + start - pointerX, 0, _mixer.ScrollableWidth));
    }
    private void MixerPanUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _mixerPanStart is null) return;
        EndMixerPan(); e.Handled = true;
    }
    private void MixerPanLostCapture(object sender, MouseEventArgs e) => EndMixerPan();
    private void MixerPanLostFocus(object sender, KeyboardFocusChangedEventArgs e) => EndMixerPan();
    private void MixerPanDeactivated(object? sender, EventArgs e) => EndMixerPan();
    private void EndMixerPan()
    {
        if (_mixerPanStart is null) return;
        _mixerPanStart = null; _mixer.Cursor = _mixerPanCursor;
        if (_mixer.IsMouseCaptured) _mixer.ReleaseMouseCapture();
    }
    private void DisposeMixerPanning()
    {
        EndMixerPan(); _owner.Deactivated -= MixerPanDeactivated;
        _mixer.PreviewMouseDown -= MixerPanDown; _mixer.PreviewMouseMove -= MixerPanMove; _mixer.PreviewMouseUp -= MixerPanUp;
        _mixer.LostMouseCapture -= MixerPanLostCapture; _mixer.LostKeyboardFocus -= MixerPanLostFocus;
    }
}
