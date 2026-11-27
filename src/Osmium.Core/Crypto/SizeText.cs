using System.Globalization;

namespace Osmium.Core.Crypto;

/// <summary>Sizes in messages about memory, in binary units: "512 bytes", "64 KiB", "1.8 GiB".</summary>
internal static class SizeText
{
    public static string Format(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes",
        < 1 << 20 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KiB",
        < 1 << 30 => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1073741824.0).ToString("#,0.##", CultureInfo.InvariantCulture) + " GiB",
    };

    /// <summary>An exact amount of KiB in the largest unit that divides it: "1 GiB", "256 MiB", "4,100 KiB".</summary>
    public static string ExactKiB(long kib) =>
        kib != 0 && kib % (1 << 20) == 0 ? (kib >> 20).ToString("N0", CultureInfo.InvariantCulture) + " GiB"
        : kib != 0 && kib % (1 << 10) == 0 ? (kib >> 10).ToString("N0", CultureInfo.InvariantCulture) + " MiB"
        : kib.ToString("N0", CultureInfo.InvariantCulture) + " KiB";
}
