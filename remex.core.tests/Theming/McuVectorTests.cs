using System.Globalization;
using System.Text.Json;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>Spec § 5: every tuple in mcu-vectors.json → McuScheme.Build → every role equals the phone's library, as ARGB integers. Zero tolerance.</summary>
public class McuVectorTests
{
    internal static uint ArgbOf(string hex) => uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) | (hex.Length == 7 ? 0xFF000000u : 0u);

    [Fact]
    public void EveryRoleOfEveryTupleEqualsTheVector()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(McuVectorFixtureTests.FixturePath()));
        var roles = doc.RootElement.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToArray();
        Assert.Equal(MaterialRoles.RoleNames, roles);

        var failures = new List<string>();
        int tuples = 0;
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            tuples++;
            var seed = v.GetProperty("seed").GetString()!;
            var wire = v.GetProperty("variant").GetString()!;
            bool dark = v.GetProperty("dark").GetBoolean();
            double contrast = v.GetProperty("contrast").GetDouble();
            Assert.True(SchemeVariantWire.TryFromWire(wire, out var variant), $"unknown wire variant {wire}");

            var actual = McuScheme.Build(ArgbOf(seed), variant, dark, contrast);
            var expected = v.GetProperty("argb").EnumerateArray().Select(a => a.GetString()!).ToArray();
            for (int i = 0; i < roles.Length; i++)
            {
                uint want = ArgbOf(expected[i]);
                uint got = actual[roles[i]];
                if (got != want)
                    failures.Add($"{seed} {wire} dark={dark} contrast={contrast.ToString(CultureInfo.InvariantCulture)} {roles[i]}: expected {expected[i]}, got #{got:X8}");
            }
        }

        Assert.Equal(720, tuples);
        Assert.True(failures.Count == 0, $"{failures.Count} mismatches; first 20:\n{string.Join('\n', failures.Take(20))}");
    }

    [Fact]
    public void TheRecordMirrorsTheGeneratorRoleForRole()
    {
        var roles = McuScheme.Build(0xFF6750A4u, SchemeVariant.TonalSpot, isDark: false, contrastLevel: 0.0);
        Assert.Equal(McuVectorFixtureTests.RoleNames, MaterialRoles.RoleNames);
        Assert.Equal(62, MaterialRoles.RoleNames.Length);
        Assert.Equal(roles.Primary, roles["primary"]);
        Assert.Equal(roles.OnTertiaryFixedVariant, roles["onTertiaryFixedVariant"]);
        Assert.Throws<ArgumentException>(() => roles["notARole"]);
    }
}
