using System.Reflection;
using WinBatLens.Services;
using Xunit;

namespace WinBatLens.Tests;

public sealed class OptimizationContractTests
{
    [Fact]
    public void RealTimePowerServiceKeepsOnlyConsumedPerformanceCounterFields()
    {
        string[] counterFields = typeof(RealTimePowerService)
            .GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType.FullName is
                "System.Diagnostics.PerformanceCounter" or
                "System.Diagnostics.PerformanceCounterCategory")
            .Select(field => field.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "_cpuCounter", "_gpuEngineCategory" }, counterFields);
    }

    [Theory]
    [InlineData("GetWifiThroughputKbps")]
    [InlineData("GetSystemRamInfo")]
    public void RemovedTelemetryHelpersCannotRegressIntoThePollingService(string methodName)
    {
        MethodInfo? method = typeof(RealTimePowerService).GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.Null(method);
    }
}
