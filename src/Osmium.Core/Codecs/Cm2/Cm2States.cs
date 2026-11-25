namespace Osmium.Core.Codecs.Cm2;

/// <summary>
/// Bit-history state machine for cm2 (indirect context models), built after the published paq8/lpaq design but
/// generated here by Osmium's own code. A state stands for the bits seen in one context:
/// <list type="bullet">
/// <item>state 0: nothing seen yet;</item>
/// <item>up to 4 bits: the exact bit sequence;</item>
/// <item>more bits: the pair (n0, n1) of zero and one counts, plus the last bit when both counts are non-zero and
/// n0 + n1 &lt; 16. Counts are bounded (the smaller one by 4, the larger by 41 down to 4 as the smaller grows);
/// when a bit is seen, a large count of the opposite bit is discounted, so recent bits weigh more.</item>
/// </list>
/// The table is generated deterministically with integer arithmetic only; it has 251 reachable states.
/// </summary>
internal static class Cm2States
{
    /// <summary>Next state: <c>Next[state * 2 + bit]</c>.</summary>
    public static readonly byte[] Next = new byte[512];
    /// <summary>Zero and one counts represented by each state.</summary>
    public static readonly byte[] N0 = new byte[256], N1 = new byte[256];
    /// <summary>Replacement priority of a slot whose first node is in this state (bits seen, saturating).</summary>
    public static readonly byte[] Priority = new byte[256];
    public static readonly int Count;

    // round(16 * log2(x)) / 6 - 1 for x > 2: how much of a count survives when the opposite bit is seen.
    private static readonly byte[] Discounted =
    [
        0, 1, 2, 3, 4, 5, 5, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 12, 12, 12,
        12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13,
    ];

    // Largest count allowed for the larger of (n0, n1), indexed by the smaller one (totals above 4).
    private static readonly int[] MaxLarger = [41, 40, 12, 5, 4];

    private readonly record struct Desc(int N0, int N1, int Hist, int Len);

    static Cm2States()
    {
        var order = new List<Desc> { new(0, 0, 0, 0) };
        var index = new Dictionary<(int, int, int, int), int> { [Key(order[0])] = 0 };
        var next = new List<int>();
        for (int i = 0; i < order.Count; i++)
        {
            for (int bit = 0; bit < 2; bit++)
            {
                var t = Step(order[i], bit);
                var k = Key(t);
                if (!index.TryGetValue(k, out int s))
                {
                    s = order.Count;
                    index[k] = s;
                    order.Add(t);
                }
                next.Add(s);
            }
        }
        if (order.Count > 256) throw new InvalidOperationException("cm2 state table overflow");
        Count = order.Count;
        for (int s = 0; s < order.Count; s++)
        {
            Next[s * 2] = (byte)next[s * 2];
            Next[s * 2 + 1] = (byte)next[s * 2 + 1];
            N0[s] = (byte)order[s].N0;
            N1[s] = (byte)order[s].N1;
            Priority[s] = (byte)Math.Min(255, order[s].N0 + order[s].N1);
        }
    }

    // Exact histories are told apart by their bit sequence; longer ones by their counts and, if kept, the last bit.
    private static (int, int, int, int) Key(Desc d)
    {
        int total = d.N0 + d.N1;
        if (total <= 4) return (0, d.Hist, d.Len, 0);
        if (d.N0 > 0 && d.N1 > 0 && total < 16) return (1, d.N0, d.N1, d.Hist & 1);
        return (2, d.N0, d.N1, 0);
    }

    private static Desc Step(Desc d, int bit)
    {
        if (d.N0 + d.N1 < 4)
            return new Desc(d.N0 + (bit ^ 1), d.N1 + bit, (d.Hist << 1) | bit, d.Len + 1);
        int n0 = d.N0, n1 = d.N1;
        if (bit != 0) { n1++; n0 = Discount(n0); }
        else { n0++; n1 = Discount(n1); }
        // Bring the pair back into the representable region, keeping roughly the same ratio.
        bool swap = n0 < n1;
        int x = swap ? n1 : n0, y = swap ? n0 : n1;
        while (!Representable(x, y))
        {
            if (y < 2) x--;
            else
            {
                x = (x * (y - 1) + y / 2) / y;
                y--;
            }
        }
        (n0, n1) = swap ? (y, x) : (x, y);
        if (n0 + n1 <= 4) throw new InvalidOperationException("cm2 state table: count pair collapsed");
        return new Desc(n0, n1, bit, 1);
    }

    private static int Discount(int x) => x <= 2 ? x : Discounted[Math.Min(x, Discounted.Length - 1)];

    private static bool Representable(int larger, int smaller) =>
        larger + smaller <= 4 || (smaller < MaxLarger.Length && larger <= MaxLarger[smaller]);
}
