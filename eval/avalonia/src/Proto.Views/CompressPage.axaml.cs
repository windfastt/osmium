using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Proto.Views;

public sealed partial class CompressPage : UserControl
{
    public IReadOnlyList<Stat> Stats { get; } =
    [
        new("166.9 MB", "in 5 items, 418 files", "read from disk"),
        new("≈ 64.7 MB", "estimated archive", "38.8 % of the original"),
        new("−5.1 %", "vs 7-Zip -mx9", "estimate on samples, Ultra"),
        new("1 min 40 s", "estimated time", "4 cores, worker off"),
    ];

    public IReadOnlyList<FileRow> Files { get; } =
    [
        new("OsmiumSetup-x64.exe", "Program, x86-64", "48.2 MB", "x86 → lzma2:d64m", "13.1 MB"),
        new("include/ (412 files)", "C/C++ headers", "52.0 MB", "dedup → text → cm2", "6.9 MB"),
        new("写真-2025.zip", "ZIP archive", "31.7 MB", "store", "31.7 MB"),
        new("libcore-arm64.dll", "Library, ARM64", "12.9 MB", "arm64 → lzma2:d32m", "3.6 MB"),
        new("telemetry.wav", "Audio, PCM 16-bit", "22.1 MB", "audio → cm", "9.4 MB"),
    ];

    public CompressPage()
    {
        DataContext = this;
        AvaloniaXamlLoader.Load(this);
        RowDrag.Attach(this.FindControl<ListBox>("FileList")!);
    }
}
