#nullable enable

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Revu.App.Helpers;

internal sealed class HoverTiltController
{
    private const double RestThreshold = 0.02;

    private readonly FrameworkElement _host;
    private readonly PlaneProjection _projection;
    private readonly double _maxTiltDegrees;
    private readonly double _hoverLiftY;
    private readonly double _hoverDepthZ;
    private readonly double _smoothing;

    private double _currentRotationX;
    private double _currentRotationY;
    private double _currentOffsetY;
    private double _currentOffsetZ;
    private double _targetRotationX;
    private double _targetRotationY;
    private double _targetOffsetY;
    private double _targetOffsetZ;
    private bool _renderingAttached;

    public HoverTiltController(
        FrameworkElement host,
        UIElement target,
        double maxTiltDegrees,
        double hoverLiftY,
        double hoverDepthZ,
        double smoothing = 0.18)
    {
        _host = host;
        _maxTiltDegrees = maxTiltDegrees;
        _hoverLiftY = hoverLiftY;
        _hoverDepthZ = hoverDepthZ;
        _smoothing = smoothing;
        _projection = target.Projection as PlaneProjection ?? new PlaneProjection();
        _projection.CenterOfRotationX = 0.5;
        _projection.CenterOfRotationY = 0.5;
        _projection.LocalOffsetZ = 0;
        target.Projection = _projection;
        Apply();
    }

    public void UpdatePointer(Point position)
    {
        if (_host.ActualWidth <= 0 || _host.ActualHeight <= 0)
        {
            return;
        }

        var x = Clamp(position.X / _host.ActualWidth);
        var y = Clamp(position.Y / _host.ActualHeight);
        _targetRotationY = (x - 0.5) * (_maxTiltDegrees * 2.0);
        _targetRotationX = -(y - 0.5) * (_maxTiltDegrees * 2.0);
        _targetOffsetY = _hoverLiftY;
        _targetOffsetZ = _hoverDepthZ;
        EnsureRendering();
    }

    public void Relax()
    {
        _targetRotationX = 0.0;
        _targetRotationY = 0.0;
        _targetOffsetY = 0.0;
        _targetOffsetZ = 0.0;
        EnsureRendering();
    }

    public void Reset()
    {
        StopRendering();
        _currentRotationX = 0.0;
        _currentRotationY = 0.0;
        _currentOffsetY = 0.0;
        _currentOffsetZ = 0.0;
        _targetRotationX = 0.0;
        _targetRotationY = 0.0;
        _targetOffsetY = 0.0;
        _targetOffsetZ = 0.0;
        Apply();
    }

    private void EnsureRendering()
    {
        if (_renderingAttached)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _renderingAttached = true;
    }

    private void StopRendering()
    {
        if (!_renderingAttached)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _renderingAttached = false;
    }

    private void OnRendering(object? sender, object e)
    {
        _currentRotationX = Lerp(_currentRotationX, _targetRotationX, _smoothing);
        _currentRotationY = Lerp(_currentRotationY, _targetRotationY, _smoothing);
        _currentOffsetY = Lerp(_currentOffsetY, _targetOffsetY, _smoothing);
        _currentOffsetZ = Lerp(_currentOffsetZ, _targetOffsetZ, _smoothing);
        Apply();

        if (!IsSettled())
        {
            return;
        }

        _currentRotationX = _targetRotationX;
        _currentRotationY = _targetRotationY;
        _currentOffsetY = _targetOffsetY;
        _currentOffsetZ = _targetOffsetZ;
        Apply();

        if (Math.Abs(_targetRotationX) < RestThreshold &&
            Math.Abs(_targetRotationY) < RestThreshold &&
            Math.Abs(_targetOffsetY) < RestThreshold &&
            Math.Abs(_targetOffsetZ) < RestThreshold)
        {
            StopRendering();
        }
    }

    private void Apply()
    {
        _projection.RotationX = _currentRotationX;
        _projection.RotationY = _currentRotationY;
        _projection.GlobalOffsetY = _currentOffsetY;
        _projection.GlobalOffsetZ = _currentOffsetZ;
    }

    private bool IsSettled()
    {
        return Math.Abs(_currentRotationX - _targetRotationX) < RestThreshold &&
               Math.Abs(_currentRotationY - _targetRotationY) < RestThreshold &&
               Math.Abs(_currentOffsetY - _targetOffsetY) < RestThreshold &&
               Math.Abs(_currentOffsetZ - _targetOffsetZ) < RestThreshold;
    }

    private static double Clamp(double value)
    {
        return Math.Max(0.0, Math.Min(1.0, value));
    }

    private static double Lerp(double current, double target, double amount)
    {
        return current + ((target - current) * amount);
    }
}
