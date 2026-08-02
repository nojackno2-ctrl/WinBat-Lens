using WinBatLens.Services;
using Xunit;

namespace WinBatLens.Tests;

public sealed class BatteryReportParserTests
{
    private const string Report = """
        <html><body>
        <h2>COMPUTER NAME</h2><table><tr><td>ROG-TEST</td></tr></table>
        <h2>SYSTEM PRODUCT NAME</h2><table><tr><td>Test Laptop</td></tr></table>
        <h2>REPORT TIME</h2><table><tr><td>2026-08-03 12:00:00</td></tr></table>
        <h2>INSTALLED BATTERIES</h2>
        <table>
          <tr><th>NAME</th><td>ASUS Battery</td></tr>
          <tr><th>MANUFACTURER</th><td>ASUSTeK</td></tr>
          <tr><th>SERIAL NUMBER</th><td>ABC123</td></tr>
          <tr><th>CHEMISTRY</th><td>Li-ion</td></tr>
          <tr><th>DESIGN CAPACITY</th><td>76,000 mWh</td></tr>
          <tr><th>FULL CHARGE CAPACITY</th><td>56,000 mWh</td></tr>
          <tr><th>CYCLE COUNT</th><td>0</td></tr>
        </table>
        <h2>BATTERY CAPACITY HISTORY</h2>
        <table>
          <tr><th>PERIOD</th><th>FULL CHARGE CAPACITY</th><th>DESIGN CAPACITY</th></tr>
          <tr><td>2026-07-12
            - 2026-07-19</td><td>55,900 mWh</td><td>76,000 mWh</td></tr>
        </table>
        </body></html>
        """;

    [Fact]
    public void ParsesSpecsHistoryAndNormalizesWhitespace()
    {
        var report = BatteryReportParser.Parse(Report);

        Assert.Equal("ROG-TEST", report.SystemInfo.ComputerName);
        Assert.Equal(76_000, report.BatterySpecs.DesignCapacity);
        Assert.Equal(56_000, report.BatterySpecs.FullChargeCapacity);
        Assert.Null(report.BatterySpecs.CycleCount);
        Assert.True(report.HealthMetrics.HasBattery);
        Assert.Equal(73.7, report.HealthMetrics.HealthPercent, 1);
        var history = Assert.Single(report.CapacityHistory);
        Assert.Equal("2026-07-12 - 2026-07-19", history.Period);
        Assert.Equal(73.6, history.HealthPercent, 1);
    }

    [Fact]
    public void OverlaysCompatibleLiveDriverPackInfo()
    {
        var pack = new BatteryTelemetryService.PackInfo
        {
            IsValid = true,
            DesignedCapacityMWh = 75_998,
            FullChargedCapacityMWh = 56_032,
            CycleCount = 123,
            Chemistry = "LION",
            ManufactureDate = new DateTime(2023, 1, 2)
        };

        var report = BatteryReportParser.Parse(Report, pack);

        Assert.Equal(75_998, report.BatterySpecs.DesignCapacity);
        Assert.Equal(56_032, report.BatterySpecs.FullChargeCapacity);
        Assert.Equal(123, report.BatterySpecs.CycleCount);
        Assert.True(report.BatterySpecs.CapacitiesFromDriver);
        Assert.Equal(new DateTime(2023, 1, 2), report.BatterySpecs.ManufactureDate);
        Assert.Equal(73.7, report.HealthMetrics.HealthPercent, 1);
    }

    [Fact]
    public void DoesNotMixDriverMwhWithReportMah()
    {
        const string mahReport = """
            <h2>INSTALLED BATTERIES</h2><table>
            <tr><th>DESIGN CAPACITY</th><td>10,000 mAh</td></tr>
            <tr><th>FULL CHARGE CAPACITY</th><td>9,000 mAh</td></tr>
            </table>
            """;
        var pack = new BatteryTelemetryService.PackInfo
        {
            IsValid = true,
            DesignedCapacityMWh = 80_000,
            FullChargedCapacityMWh = 50_000
        };

        var report = BatteryReportParser.Parse(mahReport, pack);

        Assert.Equal("mAh", report.BatterySpecs.Unit);
        Assert.Equal(10_000, report.BatterySpecs.DesignCapacity);
        Assert.Equal(9_000, report.BatterySpecs.FullChargeCapacity);
        Assert.False(report.BatterySpecs.CapacitiesFromDriver);
    }

    [Fact]
    public void ReportsNeutralDiagnosticsWhenNoBatteryExists()
    {
        var report = BatteryReportParser.Parse("<html><body><h1>Desktop report</h1></body></html>");

        Assert.False(report.HealthMetrics.HasBattery);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("info", diagnostic.Type);
        Assert.Contains("未偵測到電池", diagnostic.Title);
    }
}
