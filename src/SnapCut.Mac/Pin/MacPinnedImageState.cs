namespace SnapCut.Mac.Pin;

internal sealed record MacPinnedImageState(
    Guid Id,
    string ImagePath,
    int X,
    int Y,
    double Zoom,
    double Opacity,
    bool Hidden);
