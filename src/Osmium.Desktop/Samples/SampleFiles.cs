using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Osmium.Core.Pipeline;
using Osmium.Core.Planning;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Samples;

/// <summary>
/// The files the UI self-test compresses for its screenshots: one of each kind Osmium treats differently (text,
/// a CSV table, an uncompressed 24-bit image, 16-bit PCM audio). They are generated the same way on every run, from
/// fixed seeds and Osmium's own texts; nothing depends on the clock or the machine. (The self-test adds a copy of
/// Osmium.Core.dll and a PNG chart, which need the app folder and System.Drawing.)
/// </summary>
public static class SampleFiles
{
    public const string Notes = "notes.txt", Measurements = "measurements.csv", Gradient = "gradient.bmp", Tone = "tone.wav";

    /// <summary>Writes the four generated files into <paramref name="dir"/> and returns their paths.</summary>
    /// <param name="licenseText">Osmium's licence, quoted at the top of notes.txt when available.</param>
    public static IReadOnlyList<string> Write(string dir, string? licenseText = null)
    {
        Directory.CreateDirectory(dir);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var files = new (string Name, byte[] Data)[]
        {
            (Notes, utf8.GetBytes(NotesText(200 << 10, licenseText))),
            (Measurements, utf8.GetBytes(MeasurementsCsv(300 << 10))),
            (Gradient, GradientBmp(400, 300)),
            (Tone, ToneWav(2.0)),
        };
        var paths = new List<string>();
        foreach (var (name, data) in files)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// About <paramref name="targetChars"/> characters of README-style text: the licence, Osmium's profiles, codecs,
    /// filters and settings as described in the code, then a word chain over those sections (seeded) to reach the
    /// size with the statistics of real prose rather than of repeated paragraphs.
    /// </summary>
    public static string NotesText(int targetChars, string? licenseText = null)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append("\r\n");
        void Heading(string s) { Line(s); Line(new string('-', s.Length)); }

        Line("Osmium - notes written by the UI self-test");
        Line();
        if (!string.IsNullOrWhiteSpace(licenseText))
        {
            Heading("License");
            foreach (var l in licenseText.Trim().Split('\n')) Line(l.TrimEnd('\r'));
            Line();
        }
        Heading("Profiles");
        foreach (var p in Profile.All) Line($"* {p.Name}: {p.Description}");
        Line();
        Heading("Codecs and filters");
        foreach (var m in MethodRegistry.Default.All)
        {
            Line($"* {m.DisplayName} ({m.Name}): {m.Description}");
            foreach (var d in m.Params) Line($"  - {m.Name}.{d.Name}: {d.Label}. {d.Description}");
        }
        Line();
        Heading("Settings");
        foreach (var d in SettingReflector.Describe(new OsmiumSettings()))
            Line($"* {d.Category} / {d.Label}" + (string.IsNullOrEmpty(d.Description) ? "" : ": " + d.Description));
        Line();

        string[] words = sb.ToString().Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var next = new Dictionary<(string, string), List<string>>();
        for (int i = 0; i + 2 < words.Length; i++)
        {
            if (!next.TryGetValue((words[i], words[i + 1]), out var list)) next[(words[i], words[i + 1])] = list = [];
            list.Add(words[i + 2]);
        }
        Heading("Word chain over the sections above (filler for compression tests)");
        var rng = new Random(20_261_124);
        string a = words[0], b = words[1];
        int column = 0;
        while (sb.Length < targetChars)
        {
            if (!next.TryGetValue((a, b), out var options))
            {
                int k = rng.Next(words.Length - 1);
                (a, b) = (words[k], words[k + 1]);
                continue;
            }
            string w = options[rng.Next(options.Count)];
            if (column > 0 && column + 1 + w.Length > 100) { sb.Append("\r\n"); column = 0; }
            if (column > 0) { sb.Append(' '); column++; }
            sb.Append(w);
            column += w.Length;
            (a, b) = (b, w);
        }
        return sb.Append("\r\n").ToString();
    }

    /// <summary>A sensor log of about <paramref name="targetChars"/> characters: four sensors, a reading every 15 s.</summary>
    public static string MeasurementsCsv(int targetChars)
    {
        var rng = new Random(46_510);
        string[] sensors = ["north", "east", "south", "west"];
        var sb = new StringBuilder("time_s,sensor,temperature_c,humidity_pct,pressure_hpa\r\n");
        for (int i = 0; sb.Length < targetChars; i++)
        {
            int s = i % sensors.Length;
            double t = i / sensors.Length * 15.0;
            double temperature = 21.5 + 3.0 * Math.Sin(t / 7200.0 + s) + (rng.NextDouble() - 0.5) * 0.4;
            double humidity = 48.0 + 9.0 * Math.Cos(t / 10800.0 + s * 0.7) + (rng.NextDouble() - 0.5) * 1.5;
            double pressure = 1013.25 + 1.8 * Math.Sin(t / 43200.0) + (rng.NextDouble() - 0.5) * 0.3;
            sb.Append(CultureInfo.InvariantCulture, $"{t:0},{sensors[s]},{temperature:0.00},{humidity:0.0},{pressure:0.00}\r\n");
        }
        return sb.ToString();
    }

    /// <summary>An uncompressed 24-bit BMP: blue left to right, green top to bottom, red along the diagonal, with
    /// a little seeded noise like a photo has.</summary>
    public static byte[] GradientBmp(int width, int height)
    {
        int row = (width * 3 + 3) & ~3, pixels = row * height;
        var b = new byte[54 + pixels];
        b[0] = (byte)'B';
        b[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(2), b.Length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(10), 54);       // pixel data offset
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(14), 40);       // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(22), height);   // positive: rows stored bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(26), 1);        // planes
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(28), 24);       // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(34), pixels);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(38), 2835);     // 72 dpi
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(42), 2835);
        var rng = new Random(1_180_760);
        for (int y = 0; y < height; y++)
        {
            int o = 54 + (height - 1 - y) * row;
            for (int x = 0; x < width; x++)
            {
                b[o + 3 * x] = Channel(255.0 * x / (width - 1), rng);
                b[o + 3 * x + 1] = Channel(255.0 * y / (height - 1), rng);
                b[o + 3 * x + 2] = Channel(255.0 * (x + y) / (width + height - 2), rng);
            }
        }
        return b;
    }

    private static byte Channel(double v, Random rng) => (byte)Math.Clamp((int)Math.Round(v) + rng.Next(-2, 3), 0, 255);

    /// <summary>16-bit stereo PCM at 44.1 kHz: 440 Hz left and 660 Hz right with a third harmonic, 50 ms fades and
    /// seeded dither.</summary>
    public static byte[] ToneWav(double seconds, int rate = 44_100)
    {
        int frames = (int)(seconds * rate), dataBytes = frames * 4;
        var b = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(b.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1);        // PCM
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), 2);        // channels
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * 4); // bytes per second
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), 4);        // bytes per frame
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 16);       // bits per sample
        "data"u8.CopyTo(b.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), dataBytes);
        var rng = new Random(44_100);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate, fade = Math.Min(1.0, Math.Min(t, seconds - t) / 0.05);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(44 + 4 * i), Sample(440, t, fade, rng));
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(46 + 4 * i), Sample(660, t, fade, rng));
        }
        return b;
    }

    private static short Sample(double hz, double t, double fade, Random rng)
    {
        double v = fade * (9000 * Math.Sin(2 * Math.PI * hz * t) + 1200 * Math.Sin(2 * Math.PI * 3 * hz * t));
        return (short)Math.Clamp(Math.Round(v + rng.NextDouble() - rng.NextDouble()), short.MinValue, short.MaxValue);
    }
}
