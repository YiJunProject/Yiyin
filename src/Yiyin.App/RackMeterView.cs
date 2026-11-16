using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace LightDeck.App;

internal sealed class RackMeterView : FrameworkElement
{
    private static readonly Brush BackgroundInk = Freeze("#141C24"), Green = Freeze("#20C974"), Yellow = Freeze("#F1CF35"), Orange = Freeze("#F69B36"), Red = Freeze("#EE484E"), Over = Freeze("#A91E3D"), Hold = Freeze("#FFFFFF"), Average = Freeze("#39D8FF");
    private static readonly (double Low, double High, Brush Ink)[] Segments = [(-90, -24, Green), (-24, -12, Yellow), (-12, -3, Orange), (-3, 0, Red), (0, 12, Over)];
    private RackUiMeter _sample = new();
    private RackUiMeter? _lastSample;
    private readonly double[] _held = [-90, -90];
    private readonly long[] _expires = [0, 0];
    private readonly RackRmsBallistics[] _rms = [new(), new()];
    private long _lastTick = Stopwatch.GetTimestamp();
    internal double DisplayRmsLeft => _rms[0].Amplitude;
    internal double DisplayRmsRight => _rms[1].Amplitude;
    public RackMeterView() { ToolTip = "信号 dBFS · 白线为峰值保持；青线为 RMS 300 ms 能量平均，上升 150 ms / 回落 500 ms 平滑；不是 LUFS。"; IsHitTestVisible = true; }
    private static Brush Freeze(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    public void Update(RackUiMeter sample)
    {
        var now = Stopwatch.GetTimestamp();
        Update(sample, Math.Max(0, (now - _lastTick) / (double)Stopwatch.Frequency), now);
    }
    // Deterministic UI replay uses the same update path; it never alters engine/audio state.
    internal void Update(RackUiMeter sample, TimeSpan elapsed) => Update(sample,
        Math.Max(0, elapsed.TotalSeconds), _lastTick + (long)(Math.Max(0, elapsed.TotalSeconds) * Stopwatch.Frequency));
    private void Update(RackUiMeter sample, double elapsedSeconds, long now)
    {
        var elapsed = Math.Clamp(elapsedSeconds, 0, .1); _lastTick = now;
        for (var channel = 0; channel < 2; channel++)
        {
            var peak = RackDbScale.Db(channel == 0 ? sample.PeakLeft : sample.PeakRight);
            if (!ReferenceEquals(sample, _lastSample) && peak >= _held[channel]) { _held[channel] = peak; _expires[channel] = now + Stopwatch.Frequency; }
            else if (now > _expires[channel]) _held[channel] = Math.Max(peak, _held[channel] - elapsed * 18);
            _rms[channel].Update(channel == 0 ? sample.RmsLeft : sample.RmsRight, elapsedSeconds);
        }
        _lastSample = sample; _sample = sample; InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var top = RackDbScale.Y(12, ActualHeight); var bottom = RackDbScale.Y(-90, ActualHeight); var width = Math.Max(2, (ActualWidth - 4) / 2);
        for (var channel = 0; channel < 2; channel++)
        {
            var x = channel * (width + 4); dc.DrawRectangle(BackgroundInk, null, new Rect(x, top, width, Math.Max(0, bottom - top)));
            var level = RackDbScale.Db(channel == 0 ? _sample.PeakLeft : _sample.PeakRight);
            foreach (var segment in Segments)
            {
                if (level <= segment.Low) continue; var y = RackDbScale.Y(Math.Min(level, segment.High), ActualHeight); var end = RackDbScale.Y(segment.Low, ActualHeight);
                dc.DrawRectangle(segment.Ink, null, new Rect(x + 1, y, Math.Max(1, width - 2), Math.Max(0, end - y)));
            }
            var rms = RackDbScale.Db(_rms[channel].Amplitude);
            if (rms > -90) dc.DrawRectangle(Average, null, new Rect(x, RackDbScale.Y(rms, ActualHeight) - 1, width, 2));
            if (_held[channel] > -90) dc.DrawRectangle(Hold, null, new Rect(x, RackDbScale.Y(_held[channel], ActualHeight) - 1, width, 2));
        }
    }
}

// Display-only ballistics. Average squared linear amplitudes, never dB or amplitudes.
// Input is the latest native interval RMS held between UI refreshes, so this is a
// wall-time display average, not a replacement for the native sample-weighted RMS.
internal sealed class RackRmsBallistics
{
    internal const double WindowSeconds = .300, AttackSeconds = .150, ReleaseSeconds = .500;
    private readonly Queue<(double Energy, double Duration)> _history = new(32);
    private double _headEnergy, _headDuration, _duration, _energyTime, _displayEnergy;
    private bool _initialized;
    internal double Amplitude => Math.Sqrt(Math.Max(0, _displayEnergy));
    internal double MeanSquare => Math.Max(0, _energyTime / WindowSeconds);

    internal void Update(double amplitude, double elapsedSeconds)
    {
        var energy = amplitude >= 0 && double.IsFinite(amplitude) ? amplitude * amplitude : 0;
        if (!double.IsFinite(energy)) energy = 0;
        if (!_initialized)
        {
            Seed(energy); _displayEnergy = energy; _initialized = true; return;
        }
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return;
        if (elapsedSeconds >= WindowSeconds) Seed(energy);
        else
        {
            _history.Enqueue((energy, elapsedSeconds));
            _duration += elapsedSeconds; _energyTime += energy * elapsedSeconds;
            var excess = _duration - WindowSeconds;
            while (excess > 1e-12)
            {
                if (_headDuration <= 1e-12) (_headEnergy, _headDuration) = _history.Dequeue();
                var removed = Math.Min(excess, _headDuration);
                _headDuration -= removed; _duration -= removed; excess -= removed;
                _energyTime -= _headEnergy * removed;
            }
        }
        var target = MeanSquare;
        var tau = target > _displayEnergy ? AttackSeconds : ReleaseSeconds;
        _displayEnergy += (target - _displayEnergy) * (1 - Math.Exp(-elapsedSeconds / tau));
    }
    private void Seed(double energy)
    {
        _history.Clear(); _headEnergy = energy; _headDuration = _duration = WindowSeconds;
        _energyTime = energy * WindowSeconds;
    }
}
