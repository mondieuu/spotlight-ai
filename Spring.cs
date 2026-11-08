using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Odak;

/// <summary>
/// Spring animation in the spirit of Apple's "Designing Fluid Interfaces": defined not by duration but by
/// <b>response (seconds)</b> and <b>damping ratio</b>.
/// It always continues from the current on-screen value and velocity, so it can be interrupted and
/// redirected (when the target changes, the motion flows to the new target without a jump).
/// </summary>
public sealed class Spring
{
    private static readonly List<Spring> Active = new();
    private static TimeSpan _last;
    private static bool _hooked;

    private readonly Action<double> _apply;
    private double _response, _damping;
    private Action? _settled;

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public bool IsMoving => Active.Contains(this);

    public Spring(double initial, Action<double> apply, double response = 0.35, double damping = 1.0)
    {
        Value = Target = initial;
        _apply = apply;
        _response = response;
        _damping = damping;
    }

    /// <summary>Head toward a new target (current value and velocity are kept).</summary>
    public void To(double target, double? response = null, double? damping = null, Action? settled = null)
    {
        Target = target;
        if (response is { } r) _response = r;
        if (damping is { } d) _damping = d;
        _settled = settled;
        if (!Active.Contains(this)) Active.Add(this);
        Hook();
    }

    /// <summary>Settle instantly without animation (e.g. when "Reduce motion" is on).</summary>
    public void Snap(double value)
    {
        Active.Remove(this);
        Value = Target = value;
        Velocity = 0;
        _apply(value);
        var s = _settled;
        _settled = null;
        s?.Invoke();
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        // May be called twice in the same frame; clamp the step on the first frame and after long pauses.
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        dt = Math.Min(dt, 1 / 30.0);

        foreach (var s in Active.ToArray()) s.Step(dt);
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    private void Step(double dt)
    {
        // Response and damping → spring stiffness and friction (unit mass).
        double omega = 2 * Math.PI / _response;
        double k = omega * omega, c = 2 * _damping * omega;
        const int Sub = 4;
        double h = dt / Sub;
        for (int i = 0; i < Sub; i++)
        {
            double a = -k * (Value - Target) - c * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
        double span = Math.Max(1e-3, Math.Abs(Target) + 1);
        if (Math.Abs(Value - Target) < 0.0005 * span && Math.Abs(Velocity) < 0.005 * span)
        {
            Value = Target;
            Velocity = 0;
            Active.Remove(this);
            _apply(Value);
            var s = _settled;
            _settled = null;
            s?.Invoke();
            return;
        }
        _apply(Value);
    }
}
