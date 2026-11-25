namespace Osmium.App;

/// <summary>Spacing steps in logical pixels (96 DPI), and their size on a given control's monitor.</summary>
internal static class Space
{
    public const int Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 24, Xxl = 32;

    /// <summary>A logical length in device pixels for <paramref name="c"/>'s monitor.</summary>
    public static int Px(Control c, int logical) => c.LogicalToDeviceUnits(logical);

    /// <summary>A padding or margin given in logical pixels, in device pixels for <paramref name="c"/>'s monitor.</summary>
    public static Padding Pad(Control c, int left, int top, int right, int bottom) =>
        new(Px(c, left), Px(c, top), Px(c, right), Px(c, bottom));
}

/// <summary>Corner radii in logical pixels.</summary>
internal static class Radius
{
    public const int Control = 6, Card = 10, Meter = 4;
}
