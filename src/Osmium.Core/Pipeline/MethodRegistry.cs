using Osmium.Core.Codecs;
using Osmium.Core.Filters;
using Osmium.Core.IO;

namespace Osmium.Core.Pipeline;

/// <summary>Stable method ids. Never renumber: they are stored in archives.</summary>
public static class MethodIds
{
    public const int Store = 0, Deflate = 1, Brotli = 2, Zstd = 3, Lzma = 4, BZip2 = 5, Ppmd = 6, Cm = 7;
    public const int X86 = 64, Arm64 = 65, Delta = 66, Transpose = 67, Pcm16 = 68, Image2D = 69, Text = 70;
}

public sealed class MethodRegistry
{
    public static MethodRegistry Default { get; } = new(
    [
        new StoreCodec(), new DeflateCodec(), new BrotliCodec(), new ZstdCodec(), new LzmaCodec(),
        new BZip2Codec(), new PpmdCodec(), new CmMethod(),
        new X86Filter(), new Arm64Filter(), new DeltaFilter(), new TransposeFilter(),
        new Pcm16Filter(), new Image2DFilter(), new TextFilter(),
    ]);

    private readonly Dictionary<int, IMethod> _byId = new();
    private readonly Dictionary<string, IMethod> _byName = new(StringComparer.OrdinalIgnoreCase);

    public MethodRegistry(IEnumerable<IMethod> methods)
    {
        foreach (var m in methods)
        {
            _byId.Add(m.Id, m);
            _byName.Add(m.Name, m);
        }
    }

    public IEnumerable<IMethod> All => _byId.Values.OrderBy(m => m.Id);
    public IEnumerable<IMethod> Codecs => All.Where(m => m.Kind == MethodKind.Codec);
    public IEnumerable<IMethod> Filters => All.Where(m => m.Kind == MethodKind.Filter);
    public IMethod? ById(int id) => _byId.GetValueOrDefault(id);
    public IMethod? ByName(string name) => _byName.GetValueOrDefault(name);
    public IMethod Get(int id) => ById(id) ?? throw new KeyNotFoundException($"Method {id}");
    public IMethod Get(string name) => ByName(name) ?? throw new KeyNotFoundException($"Method '{name}'");

    public StageSpec Stage(string name, params (string, long)[] p) => new(Get(name), ParamValues.Of(p));
}

