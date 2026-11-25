namespace Osmium.App;

/// <summary>
/// A page whose layout must pass the layout audit: nothing clipped, overlapping or outside its parent at the window
/// sizes the UI self-test checks.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class LayoutStrictAttribute : Attribute;
