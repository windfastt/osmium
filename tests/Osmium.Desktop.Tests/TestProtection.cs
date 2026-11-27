using System.Runtime.CompilerServices;
using Osmium.Desktop.Settings;

namespace Osmium.Desktop.Tests;

/// <summary>
/// In this test assembly only, every password protection is PBKDF2 with the settings' iterations
/// (<see cref="SecuritySettings.UseIterationsForTests"/>), so that the tests of encrypted jobs keep the cost they had;
/// the tests of the protection itself turn it off with <see cref="AsOsmiumDoes"/>. Osmium itself never sets the switch
/// (a test checks that).
/// </summary>
internal static class TestProtection
{
#pragma warning disable CA2255 // The switch must be set before the first test, and only for this test assembly.
    [ModuleInitializer]
    internal static void UseIterationsInTests() => SecuritySettings.UseIterationsForTests = true;
#pragma warning restore CA2255

    /// <summary>Until disposed, the code this test runs (and a job manager made inside the scope) sees the protection
    /// the settings choose, as Osmium does.</summary>
    public static IDisposable AsOsmiumDoes() => new Scope();

    private sealed class Scope : IDisposable
    {
        public Scope() => SecuritySettings.UseIterationsHere = false;
        public void Dispose() => SecuritySettings.UseIterationsHere = null;
    }
}
