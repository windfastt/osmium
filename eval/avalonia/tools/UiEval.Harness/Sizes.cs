using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UiEval;

/// <summary>
/// Like-for-like publish sizes: raw folder, file count, ZIP (SmallestSize, the portable download) and 7z LZMA2 -mx9
/// solid (the closest stand-in for the Inno Setup installer's lzma2/ultra64 solid payload). Debug symbols (.pdb) are
/// left out of every number (both builds carry their own; neither ships them in the comparison).
/// Usage: UiEval.Harness sizes --out FILE.json NAME=DIR [NAME=DIR ...]
/// </summary>
internal static class Sizes
{
    public static int Run(Dictionary<string, string> opt)
    {
        var outFile = Path.GetFullPath(opt["out"]);
        var rows = new JsonArray();
        var md = new StringBuilder("| Build | Files | Folder MiB | ZIP MiB | 7z LZMA2 MiB |\n|---|---:|---:|---:|---:|\n");
        var sevenZip = new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\ProgramData\Chocolatey\bin\7z.exe" }.FirstOrDefault(File.Exists);
        foreach (var (k, v) in opt.Where(kv => kv.Key.StartsWith("arg", StringComparison.Ordinal)))
        {
            var eq = v.IndexOf('=');
            string name = v[..eq], dir = Path.GetFullPath(v[(eq + 1)..]);
            var pdbs = Directory.GetFiles(dir, "*.pdb", SearchOption.AllDirectories);
            var stage = Path.Combine(Path.GetTempPath(), $"w0-stage-{name}");
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                var dst = Path.Combine(stage, Path.GetRelativePath(dir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst);
            }
            dir = stage;
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            long total = files.Sum(f => new FileInfo(f).Length);
            var tmp = Path.Combine(Path.GetTempPath(), $"w0-size-{name}");
            if (File.Exists(tmp + ".zip")) File.Delete(tmp + ".zip");
            ZipFile.CreateFromDirectory(dir, tmp + ".zip", CompressionLevel.SmallestSize, includeBaseDirectory: false);
            long zip = new FileInfo(tmp + ".zip").Length;
            long? sz = null;
            if (sevenZip is not null)
            {
                if (File.Exists(tmp + ".7z")) File.Delete(tmp + ".7z");
                var psi = new ProcessStartInfo(sevenZip, $"a -t7z -m0=lzma2 -mx=9 -ms=on -bd \"{tmp}.7z\" \"{dir}\\*\"") { UseShellExecute = false, RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) sz = new FileInfo(tmp + ".7z").Length;
            }
            var largest = files.OrderByDescending(f => new FileInfo(f).Length).Take(5).Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length / 1048576.0:0.0} MiB");
            rows.Add(new JsonObject
            {
                ["name"] = name, ["dir"] = dir, ["pdbsLeftOut"] = pdbs.Length, ["files"] = files.Length, ["bytes"] = total, ["zipBytes"] = zip, ["sevenZipBytes"] = sz,
                ["largest"] = new JsonArray([.. largest.Select(s => (JsonNode)s)]),
            });
            md.Append($"| {name} | {files.Length} | {total / 1048576.0:0.0} | {zip / 1048576.0:0.0} | {(sz is long s ? (s / 1048576.0).ToString("0.0") : "–")} |\n");
            Console.WriteLine($"{name}: {files.Length} files, {total / 1048576.0:0.0} MiB, zip {zip / 1048576.0:0.0} MiB, 7z {(sz ?? 0) / 1048576.0:0.0} MiB");
        }
        File.WriteAllText(outFile, new JsonObject { ["rows"] = rows }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.ChangeExtension(outFile, ".md"), md.ToString());
        Console.WriteLine(md.ToString());
        return 0;
    }
}
