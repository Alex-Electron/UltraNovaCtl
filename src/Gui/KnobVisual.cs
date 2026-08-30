using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UltraNovaCtl.Gui;

/// <summary>
/// A rotary knob drawn the way the Automap editor drew them: a dark dial with an arc
/// showing how far it is turned and a pointer at the current angle. Touching the physical
/// knob lights the ring, which is what the synth's touch sensors are for.
///
/// Everything here is built once and reused. Rebuilding geometry per frame is what made
/// the window lag when a knob was spun quickly.
/// </summary>
public sealed class KnobVisual : Control
{
    // The dial sweeps 270 degrees, leaving a gap at the bottom like a real panel knob.
    const double StartAngle = 135.0;
    const double SweepAngle = 270.0;
    const int Steps = 48;               // arc resolution, fixed so nothing allocates later

    static readonly IBrush DialFill = new SolidColorBrush(Color.Parse("#101014"));
    static readonly IBrush DialFillHot = new SolidColorBrush(Color.Parse("#1C1810"));
    static readonly IPen TrackPen = new Pen(new SolidColorBrush(Color.Parse("#3A3A44")), 3);
    static readonly IPen ValuePen = new Pen(new SolidColorBrush(Color.Parse("#5AA9E6")), 3);
    static readonly IPen TouchPen = new Pen(new SolidColorBrush(Color.Parse("#E8A33D")), 3);
    static readonly IPen RimPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A33")), 1);
    static readonly IPen PointerPen = new Pen(new SolidColorBrush(Color.Parse("#E0E0E8")), 2);
    static readonly IPen PointerHot = new Pen(new SolidColorBrush(Color.Parse("#E8A33D")), 2);

    int _value;
    bool _touched;
    double _lastSize = -1;
    Point _centre;
    double _ringR, _bodyR;
    readonly Point[] _ring = new Point[Steps + 1];   // precomputed arc points

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 127;

    public int Value
    {
        get => _value;
        set { if (_value != value) { _value = value; InvalidateVisual(); } }
    }

    public bool Touched
    {
        get => _touched;
        set { if (_touched != value) { _touched = value; InvalidateVisual(); } }
    }

    /// <summary>Kept for compatibility; the dial no longer needs a separate flash state.</summary>
    public bool Active { get; set; }

    void Rebuild(double w, double h)
    {
        double size = Math.Min(w, h);
        _centre = new Point(w / 2, h / 2);
        _ringR = size / 2 - 4;
        _bodyR = _ringR - 6;
        for (int i = 0; i <= Steps; i++)
        {
            double deg = StartAngle - SweepAngle * i / Steps;
            double rad = deg * Math.PI / 180.0;
            _ring[i] = new Point(_centre.X + _ringR * Math.Cos(rad),
                                 _centre.Y - _ringR * Math.Sin(rad));
        }
        _lastSize = size;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        double size = Math.Min(b.Width, b.Height);
        if (size <= 6) return;
        if (Math.Abs(size - _lastSize) > 0.5) Rebuild(b.Width, b.Height);

        // Track, then the filled portion, as straight segments - cheap and identical
        // on screen to a real arc at this size.
        for (int i = 0; i < Steps; i++) ctx.DrawLine(TrackPen, _ring[i], _ring[i + 1]);

        double span = Math.Max(1, Maximum - Minimum);
        double t = Math.Clamp((_value - Minimum) / span, 0, 1);
        int filled = (int)(Steps * t);
        var pen = _touched ? TouchPen : ValuePen;
        for (int i = 0; i < filled; i++) ctx.DrawLine(pen, _ring[i], _ring[i + 1]);

        ctx.DrawEllipse(_touched ? DialFillHot : DialFill, RimPen, _centre, _bodyR, _bodyR);

        double angle = (StartAngle - SweepAngle * t) * Math.PI / 180.0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var tail = new Point(_centre.X + _bodyR * 0.3 * cos, _centre.Y - _bodyR * 0.3 * sin);
        var tip = new Point(_centre.X + (_bodyR - 2) * cos, _centre.Y - (_bodyR - 2) * sin);
        ctx.DrawLine(_touched ? PointerHot : PointerPen, tail, tip);
    }
}
