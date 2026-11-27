namespace GearDeck.Desktop.Services;

/// <summary>向后兼容门面：委托至共享 <see cref="SystemMetricsEngine"/>，避免与 Widget 重复实现。</summary>
public static class DesktopMetricsEngine
{
    public static IReadOnlyList<MetricDefinition> AllMetrics => SystemMetricsEngine.AllMetrics;

    public static IReadOnlyList<string> DefaultFlyoutMetricIds => SystemMetricsEngine.DefaultFlyoutMetricIds;

    public static SystemMetricsSnapshot Sample(MetricScope scope = MetricScope.All)
        => SystemMetricsEngine.Sample(scope);

    public static string FormatTaskbar(string metricId, SystemMetricsSnapshot s)
        => SystemMetricsEngine.FormatTaskbar(metricId, s);

    public static string FormatDetailed(string metricId, SystemMetricsSnapshot s)
        => SystemMetricsEngine.FormatDetailed(metricId, s);

    public static double? GetPercentage(string metricId, SystemMetricsSnapshot s)
        => SystemMetricsEngine.GetPercentage(metricId, s);

    internal static string FormatCompactSpeed(double bps)
        => SystemMetricsEngine.FormatCompactSpeed(bps);

    internal static string FormatFullSpeed(double bps)
        => SystemMetricsEngine.FormatFullSpeed(bps);

    public static double ParseMacLoadAvgPercent(string loadOutput, int processorCount)
        => SystemMetricsEngine.ParseMacLoadAvgPercent(loadOutput, processorCount);
}
