namespace Screenshot.App.Capture;

public readonly record struct ScreenRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y)
    {
        return !IsEmpty &&
               x >= X &&
               y >= Y &&
               x < X + Width &&
               y < Y + Height;
    }

    public static ScreenRegion FromCorners(int firstX, int firstY, int secondX, int secondY)
    {
        var left = Math.Min(firstX, secondX);
        var top = Math.Min(firstY, secondY);
        var right = Math.Max(firstX, secondX);
        var bottom = Math.Max(firstY, secondY);

        return new ScreenRegion(left, top, right - left, bottom - top);
    }

    public static ScreenRegion Intersect(ScreenRegion first, ScreenRegion second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Min(first.Y + first.Height, second.Y + second.Height);

        return right <= left || bottom <= top
            ? default
            : new ScreenRegion(left, top, right - left, bottom - top);
    }
}
