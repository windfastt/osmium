using Avalonia.Controls;
using Avalonia.Layout;

namespace Proto.Views;

/// <summary>The pages that are not part of the preview: a header and one line saying so.</summary>
public sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(string name)
    {
        Bind(BackgroundProperty, this.GetResourceObservable("Page"));
        var spec = new TextBlock { Text = name.ToUpperInvariant() + " · NOT IN THIS PREVIEW" };
        spec.Classes.Add("spec");
        var title = new TextBlock { Text = name };
        title.Classes.Add("title");
        var lede = new TextBlock
        {
            Text = "The preview shows the Compress and Archives pages only. This page is built in 0.2.0 on the same kit.",
        };
        lede.Classes.Add("lede");
        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(32, 22, 32, 14),
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { spec, title, lede },
        };
    }
}
