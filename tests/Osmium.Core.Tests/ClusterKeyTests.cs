using System.Globalization;
using Osmium.Core.Distributed;

namespace Osmium.Core.Tests;

/// <summary>Cluster keys are pasted between PCs through mail, chat and terminals; parsing must survive that.</summary>
public class ClusterKeyTests
{
    // Encoded independently (Python: the key alphabet as base32, SHA-256 checksum) from the master bytes 0x00..0x1F.
    private const string Vector = "OSM1-AAASE-A2EAW-DAQCA-KBJFS-2DJQB-6JBCE-SVCSL-TNF22-DEPBY-HA7D2-RYGDJ";
    private static readonly byte[] VectorMaster = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // Built from code points so the source stays plain ASCII.
    private const char NoBreakSpace = (char)0x00A0, SoftHyphen = (char)0x00AD, NonBreakingHyphen = (char)0x2011,
        EnDash = (char)0x2013, EmDash = (char)0x2014, MinusSign = (char)0x2212, SmallHyphenMinus = (char)0xFE63,
        FullwidthHyphenMinus = (char)0xFF0D, ZeroWidthSpace = (char)0x200B, ZeroWidthJoiner = (char)0x200D,
        WordJoiner = (char)0x2060, ByteOrderMark = (char)0xFEFF, LeftToRightMark = (char)0x200E, Delete = (char)0x7F,
        CyrillicCapitalA = (char)0x0410;

    /// <summary>The 55 key characters without prefix and dashes.</summary>
    private static readonly string Body = Vector["OSM1-".Length..].Replace("-", "");

    private static void AssertIsVector(string text)
    {
        var key = ClusterKey.Parse(text);
        Assert.Equal(VectorMaster, key.Master);
        Assert.Equal(Vector, key.ToString());
    }

    private static string ParseError(string text) => Assert.Throws<FormatException>(() => ClusterKey.Parse(text)).Message;

    [Fact]
    public void KnownVectorDecodesAndEncodes()
    {
        Assert.Equal(70, Vector.Length);
        AssertIsVector(Vector);
    }

    [Fact]
    public void GeneratedKeysRoundTrip()
    {
        for (int i = 0; i < 64; i++)
        {
            var key = ClusterKey.Generate();
            string text = key.ToString();
            Assert.Equal(70, text.Length);
            Assert.StartsWith("OSM1-", text, StringComparison.Ordinal);
            var back = ClusterKey.Parse(text);
            Assert.Equal(key.Master, back.Master);
            Assert.Equal(key.Tag, back.Tag);
            Assert.Equal(text, back.ToString());
        }
    }

    [Fact]
    public void LowercaseIsAccepted() => AssertIsVector(Vector.ToLowerInvariant());

    [Fact]
    public void SpacesInsteadOfDashesAndNoSeparatorsAreAccepted()
    {
        AssertIsVector(Vector.Replace('-', ' '));
        AssertIsVector(Vector.Replace("-", ""));
        AssertIsVector("  " + Vector + "  ");
    }

    [Fact]
    public void LineWrappedKeysAreAccepted()
    {
        // as pasted from an e-mail or a terminal: CRLF and LF breaks, indentation, tabs, a trailing newline
        AssertIsVector(Vector[..30] + "\r\n    " + Vector[30..52] + "\n\t" + Vector[52..] + "\r\n");
        AssertIsVector(string.Join("\n", Enumerable.Range(0, 7).Select(i => Vector.Substring(i * 10, 10))));
    }

    [Fact]
    public void TypographicDashesAndNoBreakSpacesAreAccepted()
    {
        foreach (char dash in new[] { EnDash, NonBreakingHyphen, EmDash, MinusSign, SmallHyphenMinus, FullwidthHyphenMinus })
            AssertIsVector(Vector.Replace('-', dash));
        AssertIsVector(Vector.Replace('-', NoBreakSpace));
        AssertIsVector(Vector.Replace("-", "-" + SoftHyphen));
    }

    [Fact]
    public void InvisibleCharactersAreIgnored()
    {
        AssertIsVector(ByteOrderMark + Vector);
        AssertIsVector(ZeroWidthSpace + Vector + ZeroWidthSpace);
        AssertIsVector("OS" + ZeroWidthJoiner + "M1" + Vector[4..]);                  // inside the prefix
        AssertIsVector(Vector.Replace("-", "-" + WordJoiner));
        AssertIsVector(LeftToRightMark + Vector);
        AssertIsVector(Vector.Insert(20, Delete.ToString()));                          // Ctrl+Backspace in a plain text box
    }

    [Fact]
    public void ParsingDoesNotDependOnTheCurrentCulture()
    {
        CultureInfo turkish;
        try { turkish = CultureInfo.GetCultureInfo("tr-TR"); }
        catch (CultureNotFoundException) { return; }                                  // invariant-globalization host
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = turkish;
            AssertIsVector(Vector.ToLowerInvariant());
            AssertIsVector(ZeroWidthSpace + Vector);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void TruncatedKeysAreTooShort()
    {
        string m = ParseError(Vector[..^1]);
        Assert.Contains("too short", m);
        Assert.Contains("54 of 55", m);
        Assert.Contains("too short", ParseError(Vector[..40]));
        Assert.Contains("too short", ParseError("OSM1-"));
    }

    [Fact]
    public void ExtraCharactersAreAcceptedOnlyAfterAVerifiedKey()
    {
        // 0.1.1 ignored them: a key whose first 34 bytes verify keeps working, in canonical form.
        AssertIsVector(Vector + "A");
        AssertIsVector(Vector + Vector[4..]);                                         // pasted twice
        // Not a verified key: still an error that says what is wrong.
        string bad = "OSM1-" + Body[..10] + (Body[10] == 'A' ? 'B' : 'A') + Body[11..] + "A";
        Assert.Contains("too long", ParseError(bad));
    }

    [Fact]
    public void NonZeroPadBitsAreAcceptedOnlyAfterAVerifiedKey()
    {
        // The last character carries 2 key bits and 3 pad bits: the 8 values with the same top bits are 0.1.1-equivalent.
        char last = Body[^1];
        int idx = Alphabet.IndexOf(last);
        char sameBits = Alphabet[idx ^ 1];
        AssertIsVector("OSM1-" + Body[..^1] + sameBits);
        Assert.Contains("checksum", ParseError("OSM1-" + Body[..^1] + Alphabet[idx ^ 8]));
    }

    [Fact]
    public void EveryOneCharacterTypoIsRejected()
    {
        for (int i = 0; i < Body.Length; i++)
        {
            if (i == Body.Length - 1) continue; // its low bits are padding, which 0.1.1 ignored (see NonZeroPadBits...)
            char wrong = Alphabet[(Alphabet.IndexOf(Body[i]) + 1) % Alphabet.Length];
            string typo = "OSM1-" + Body[..i] + wrong + Body[(i + 1)..];
            Assert.Contains("checksum", ParseError(typo));
        }
    }

    [Fact]
    public void SwappedNeighboursAreRejected()
    {
        for (int i = 0; i + 1 < Body.Length; i++)
        {
            if (Body[i] == Body[i + 1]) continue;
            string swapped = "OSM1-" + Body[..i] + Body[i + 1] + Body[i] + Body[(i + 2)..];
            Assert.Contains("checksum", ParseError(swapped));
        }
    }

    [Theory]
    [InlineData('O')]
    [InlineData('I')]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('o')]
    [InlineData('i')]
    public void ExcludedCharactersGiveATypoHint(char c)
    {
        string m = ParseError("OSM1-" + c + Body[1..]);
        Assert.Contains($"'{char.ToUpperInvariant(c)}'", m);
        Assert.Contains("no I, O, 0 or 1", m);
    }

    [Fact]
    public void OtherCharactersAreNamedWithTheirCodePoint()
    {
        Assert.Contains("U+0410", ParseError("OSM1-" + CyrillicCapitalA + Body[1..]));   // looks like a Latin A
        Assert.Contains("'.'", ParseError(Vector.Replace('-', '.')));
    }

    [Theory]
    [InlineData("OSM2-")]
    [InlineData("OSN1-")]
    [InlineData("SM1-")]
    [InlineData("")]
    public void WrongOrMissingPrefixIsRejected(string prefix) =>
        Assert.Contains("start with OSM1-", ParseError(prefix + Vector["OSM1-".Length..]));

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    public void EmptyKeyIsRejected(string text) => Assert.Contains("empty", ParseError(text));

    [Fact]
    public void NullIsAnArgumentError() => Assert.Throws<ArgumentNullException>(() => ClusterKey.Parse(null!));
}
