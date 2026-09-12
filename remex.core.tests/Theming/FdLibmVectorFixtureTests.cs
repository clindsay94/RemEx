using System.Security.Cryptography;
using System.Text.Json;

namespace Remex.Core.Tests.Theming;

/// <summary>
/// RemEx-4kv0g.8 (fix round 2): guards <c>Fixtures/fdlibm-vectors.json</c> — the dense JDK 21
/// StrictMath.log1p/expm1 oracle <see cref="MathUtilsLog1pExpm1Tests"/> checks bit-exactness
/// against — the same way <c>McuVectorFixtureTests</c> guards <c>mcu-vectors.json</c>: a hash pin so
/// an edit to the fixture is a visible, deliberate diff rather than a silent weakening of the oracle.
/// Regenerate with <c>.superpowers/sdd/2026-09-12-palette-parity/gen-fdlibm-vectors.py</c> against a
/// real JDK (Oracle.java / StrictMath), never by hand-editing the JSON.
/// </summary>
public class FdLibmVectorFixtureTests
{
    private const string ExpectedSha256 = "B1A556EF4C0E745C01E92CF6A07314B64C773B73F8F809525019FD79C95A6D03";

    internal static string FixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "fdlibm-vectors.json");

    [Fact]
    public void TheFixtureHashIsPinned()
    {
        var bytes = File.ReadAllBytes(FixturePath());
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Equal(ExpectedSha256, actual);
    }

    [Fact]
    public void TheFixtureHasBothFunctionsAndADenseCam16Sweep()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        Assert.True(doc.RootElement.GetProperty("log1p").GetArrayLength() > 20000);
        Assert.True(doc.RootElement.GetProperty("expm1").GetArrayLength() > 20000);
        Assert.StartsWith("Temurin 21", doc.RootElement.GetProperty("generator").GetString());
    }
}
