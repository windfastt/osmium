using System.Buffers.Binary;
using Osmium.Core.Analysis;
using Osmium.Desktop.Samples;

namespace Osmium.Desktop.Tests;

/// <summary>The UI self-test's sample folder must be the same on every run and contain what its name says.</summary>
public class SampleFilesTests
{
    [Fact]
    public void GenerationIsDeterministic()
    {
        Assert.Equal(SampleFiles.NotesText(200 << 10, "MIT License"), SampleFiles.NotesText(200 << 10, "MIT License"));
        Assert.Equal(SampleFiles.MeasurementsCsv(300 << 10), SampleFiles.MeasurementsCsv(300 << 10));
        Assert.Equal(SampleFiles.GradientBmp(400, 300), SampleFiles.GradientBmp(400, 300));
        Assert.Equal(SampleFiles.ToneWav(2.0), SampleFiles.ToneWav(2.0));
    }

    [Fact]
    public void NotesQuoteOsmiumsOwnTexts()
    {
        string notes = SampleFiles.NotesText(200 << 10, "MIT License\nCopyright (c) 2026 Osmium contributors");
        Assert.InRange(notes.Length, 200 << 10, (200 << 10) + 200);
        Assert.StartsWith("Osmium - notes written by the UI self-test\r\n", notes);
        Assert.Contains("Copyright (c) 2026 Osmium contributors\r\n", notes);
        Assert.Contains("* max: " + Core.Planning.Profile.Max.Description, notes);
        Assert.Contains("* Cluster / Cluster key: ", notes);
        Assert.DoesNotContain("\r\r", notes);
        Assert.DoesNotContain("License", SampleFiles.NotesText(1000));   // no licence text given: no licence section
    }

    [Fact]
    public void TheCsvIsATable()
    {
        var lines = SampleFiles.MeasurementsCsv(300 << 10).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("time_s,sensor,temperature_c,humidity_pct,pressure_hpa", lines[0]);
        Assert.True(lines.Length > 5000, lines.Length.ToString());
        Assert.All(lines, l => Assert.Equal(5, l.Split(',').Length));
        Assert.Equal("0,north", string.Join(",", lines[1].Split(',')[..2]));
        Assert.Equal("15,north", string.Join(",", lines[5].Split(',')[..2]));
    }

    [Fact]
    public void TheBitmapIsAn24BitBmp()
    {
        var b = SampleFiles.GradientBmp(400, 300);
        Assert.Equal(54 + 400 * 3 * 300, b.Length);
        Assert.Equal((byte)'B', b[0]);
        Assert.Equal((byte)'M', b[1]);
        Assert.Equal(b.Length, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(2)));
        Assert.Equal(400, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(18)));
        Assert.Equal(300, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(22)));
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(28)));
    }

    [Fact]
    public void TheToneIsStereo16BitPcm()
    {
        var b = SampleFiles.ToneWav(2.0);
        Assert.Equal(44 + 2 * 44_100 * 4, b.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(22)));
        Assert.Equal(44_100, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(34)));
        short peak = 0;
        for (int i = 44; i < b.Length; i += 2) peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i))));
        Assert.InRange(peak, 5000, 16000);                                // a real signal, neither silence nor clipping
    }

    [Fact]
    public void OsmiumDetectsWhatEachFileIs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osm-sample-" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var paths = SampleFiles.Write(dir, "MIT License");
            Assert.Equal(4, paths.Count);
            Assert.All(paths, p => Assert.True(File.Exists(p), p));

            var wav = Sniffer.Analyze(Path.Combine(dir, SampleFiles.Tone));
            Assert.Equal(CompatClass.Audio, wav.Class);
            Assert.Equal(2, wav.Pcm?.Channels);
            Assert.Equal(16, wav.Pcm?.Bits);

            var bmp = Sniffer.Analyze(Path.Combine(dir, SampleFiles.Gradient));
            Assert.Equal(CompatClass.Image, bmp.Class);
            Assert.Equal((400, 300, 3), (bmp.Image?.Width, bmp.Image?.Height, bmp.Image?.BytesPerPixel));

            Assert.Equal(CompatClass.Text, Sniffer.Analyze(Path.Combine(dir, SampleFiles.Notes)).Class);
            Assert.Contains(Sniffer.Analyze(Path.Combine(dir, SampleFiles.Measurements)).Class, new[] { CompatClass.Text, CompatClass.Structured });
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
