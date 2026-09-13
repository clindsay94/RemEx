using Remex.Core.Messages;
using Remex.Core.Theming;
using Remex.Core.Theming.Mcu;

namespace Remex.Core.Tests.Theming;

/// <summary>Spec B §1–2 (RemEx-4kv0g.3.1): category → family, component → tone for sensor cards.</summary>
public class SensorFamiliesTests
{
    [Fact]
    public void EveryMetricKindHasAFamily()
    {
        foreach (MetricKind k in Enum.GetValues<MetricKind>())
            Assert.True(Enum.IsDefined(SensorFamilies.For(k)));
    }

    [Theory]
    [InlineData(MetricKind.CpuLoad, SensorFamily.Primary)]
    [InlineData(MetricKind.GpuLoad, SensorFamily.Tertiary)]
    [InlineData(MetricKind.RamLoad, SensorFamily.Secondary)]
    [InlineData(MetricKind.RamUsedGb, SensorFamily.Secondary)]
    [InlineData(MetricKind.RamTotalGb, SensorFamily.Secondary)]
    [InlineData(MetricKind.CpuTempC, SensorFamily.Primary)]
    [InlineData(MetricKind.GpuTempC, SensorFamily.Tertiary)]
    [InlineData(MetricKind.TempC, SensorFamily.Tertiary)]
    [InlineData(MetricKind.ClockMhz, SensorFamily.Tertiary)]
    [InlineData(MetricKind.PowerW, SensorFamily.Tertiary)]
    [InlineData(MetricKind.FanRpm, SensorFamily.Tertiary)]
    [InlineData(MetricKind.NetThroughputMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.NetDownMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.NetUpMbps, SensorFamily.Primary)]
    [InlineData(MetricKind.VoltageV, SensorFamily.Tertiary)]
    [InlineData(MetricKind.DiskRateMBs, SensorFamily.Secondary)]
    [InlineData(MetricKind.Unknown, SensorFamily.Neutral)]
    public void TheSpecTableIsWhatShips(MetricKind kind, SensorFamily expected)
    {
        Assert.Equal(expected, SensorFamilies.For(kind));
    }

    [Fact]
    public void NextIsAThreeCycleAndNeutralJoinsAtPrimary()
    {
        Assert.Equal(SensorFamily.Secondary, SensorFamilies.Next(SensorFamily.Primary));
        Assert.Equal(SensorFamily.Tertiary, SensorFamilies.Next(SensorFamily.Secondary));
        Assert.Equal(SensorFamily.Primary, SensorFamilies.Next(SensorFamily.Tertiary));
        Assert.Equal(SensorFamily.Primary, SensorFamilies.Next(SensorFamily.Neutral));
    }

    private static readonly SensorFamily[] AllFamilies =
    {
        SensorFamily.Primary, SensorFamily.Secondary, SensorFamily.Tertiary, SensorFamily.Neutral,
    };

    [Fact]
    public void SeriesTwoNeverEqualsSeriesOne()
    {
        foreach (var f in AllFamilies)
            foreach (SchemeVariant v in Enum.GetValues<SchemeVariant>())
                Assert.NotEqual(SensorFamilies.MainRole(f), SensorFamilies.SeriesTwoRole(f, v));
    }

    [Fact]
    public void MonochromeUsesOutlineForTheColourFamiliesAndPrimaryForNeutral()
    {
        Assert.Equal("outline", SensorFamilies.SeriesTwoRole(SensorFamily.Primary, SchemeVariant.Monochrome));
        Assert.Equal("outline", SensorFamilies.SeriesTwoRole(SensorFamily.Secondary, SchemeVariant.Monochrome));
        Assert.Equal("outline", SensorFamilies.SeriesTwoRole(SensorFamily.Tertiary, SchemeVariant.Monochrome));

        foreach (SchemeVariant v in Enum.GetValues<SchemeVariant>())
            Assert.Equal("primary", SensorFamilies.SeriesTwoRole(SensorFamily.Neutral, v));
    }

    [Fact]
    public void EveryRoleNameIsAMaterialRole()
    {
        var roleNames = new HashSet<string>(MaterialRoles.RoleNames);
        foreach (var f in AllFamilies)
        {
            Assert.Contains(SensorFamilies.MainRole(f), roleNames);
            foreach (SchemeVariant v in Enum.GetValues<SchemeVariant>())
                Assert.Contains(SensorFamilies.SeriesTwoRole(f, v), roleNames);
        }
    }
}
