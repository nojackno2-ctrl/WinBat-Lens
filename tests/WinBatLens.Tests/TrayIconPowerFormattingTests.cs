using WinBatLens.Services;
using Xunit;

namespace WinBatLens.Tests;

public sealed class TrayIconPowerFormattingTests
{
    [Theory]
    [InlineData(0.1, "0.1")]
    [InlineData(0.9, "0.9")]
    public void KeepsSubWattPowerVisible(double watts, string expected)
    {
        Assert.Equal(expected, DynamicTrayIconService.FormatWattageForIcon(watts));
    }

    [Theory]
    [InlineData(1.4, "1")]
    [InlineData(49.6, "50")]
    [InlineData(100.0, "99+")]
    public void KeepsWholeWattAndOverflowFormatting(double watts, string expected)
    {
        Assert.Equal(expected, DynamicTrayIconService.FormatWattageForIcon(watts));
    }
}
