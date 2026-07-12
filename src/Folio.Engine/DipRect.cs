namespace Folio.Engine;

/// <summary>A rectangle in device-independent pixels (XAML units).</summary>
public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(DipRect other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}
