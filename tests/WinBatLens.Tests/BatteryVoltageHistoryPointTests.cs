using System;
using WinBatLens.Models;
using WinBatLens.Services;
using Xunit;

namespace WinBatLens.Tests;

public sealed class BatteryVoltageHistoryPointTests
{
    [Fact]
    public void AggregatesAverageRangeAndSampleCount()
    {
        var point = new BatteryVoltageHistoryPoint { BatteryPercent = 42 };
        var first = new DateTime(2026, 8, 5, 12, 0, 0);
        point.AddSample(12.10, first);
        point.AddSample(12.30, first.AddSeconds(1));

        Assert.Equal(42, point.BatteryPercent);
        Assert.Equal(12.20, point.AverageVoltageV, 2);
        Assert.Equal(12.10, point.MinimumVoltageV, 2);
        Assert.Equal(12.30, point.MaximumVoltageV, 2);
        Assert.Equal(2, point.SampleCount);
        Assert.Equal(first.AddSeconds(1), point.LastRecordedAt);
        Assert.Equal("12.10–12.30 V", point.VoltageRangeText);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void RejectsInvalidVoltage(double voltage)
    {
        var point = new BatteryVoltageHistoryPoint { BatteryPercent = 42 };

        Assert.Throws<ArgumentOutOfRangeException>(() => point.AddSample(voltage, DateTime.Now));
    }

    [Theory]
    [InlineData(50, 50, false)]
    [InlineData(50, 49, true)]
    [InlineData(50, 51, true)]
    [InlineData(null, 50, true)]
    public void RecordsOnlyWhenBatteryPercentChanges(int? previous, int current, bool expected)
    {
        Assert.Equal(expected, BatteryVoltageHistoryService.HasPercentChanged(previous, current));
    }
}
