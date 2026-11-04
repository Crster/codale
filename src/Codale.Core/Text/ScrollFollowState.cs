namespace Codale.Core.Text;

/// <summary>Separates viewport motion from changes to a virtualized transcript's geometry.</summary>
public sealed class ScrollFollowState
{
    private double _extent = double.NaN;
    private double _viewport = double.NaN;
    private double _offset;

    public bool IsFollowing { get; private set; } = true;

    public void Follow() => IsFollowing = true;
    public void Pause() => IsFollowing = false;

    public void Reset()
    {
        IsFollowing = true;
        _extent = _viewport = double.NaN;
        _offset = 0;
    }

    public void Observe(double offset, double extent, double viewport, bool programmatic)
    {
        var geometryChanged = Math.Abs(extent - _extent) > 0.5 || Math.Abs(viewport - _viewport) > 0.5
            || double.IsNaN(_extent);
        var moved = Math.Abs(offset - _offset) > 0.5;
        _extent = extent;
        _viewport = viewport;
        _offset = offset;

        // Only explicit input calls Pause. Inferring a pause from offset motion is
        // unsafe: native anchoring can move it even when the extent is unchanged.
        // A reader moving back to the end may resume following.
        if (!programmatic && !geometryChanged && moved && Math.Max(0, extent - viewport) - offset <= 4)
            IsFollowing = true;
    }
}
