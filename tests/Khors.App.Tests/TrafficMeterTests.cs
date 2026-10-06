using Khors.App.Services;
using Khors.Engines.Traffic;
using Xunit;

namespace Khors.App.Tests;

/// <summary>Скорость по счётчикам ядра и шкала графика.</summary>
public class TrafficMeterTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RateIsDeltaPerSecond()
    {
        var meter = new TrafficMeter();
        meter.Add(new TrafficCounters(1000, 10_000), s_start);
        Assert.Equal(default, meter.Current);
        Assert.Empty(meter.Samples);

        meter.Add(new TrafficCounters(3000, 30_000), s_start.AddSeconds(2));

        Assert.Equal(new TrafficSample(1000, 10_000), meter.Current);
        Assert.Equal(new TrafficCounters(3000, 30_000), meter.Total);
        Assert.Single(meter.Samples);
    }

    [Fact]
    public void KeepsOnlyCapacitySamples()
    {
        var meter = new TrafficMeter(capacity: 3);
        for (var i = 0; i <= 5; i++)
        {
            meter.Add(new TrafficCounters(i * 100, i * 1000), s_start.AddSeconds(i));
        }

        Assert.Equal(3, meter.Samples.Count);
    }

    [Fact]
    public void CountersGoingDownRestartTheRate()
    {
        var meter = new TrafficMeter();
        meter.Add(new TrafficCounters(5000, 5000), s_start);
        meter.Add(new TrafficCounters(100, 100), s_start.AddSeconds(1));

        Assert.Equal(default, meter.Current);
        Assert.Equal(new TrafficCounters(100, 100), meter.Total);

        meter.Add(new TrafficCounters(300, 1100), s_start.AddSeconds(2));
        Assert.Equal(new TrafficSample(200, 1000), meter.Current);
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var meter = new TrafficMeter();
        meter.Add(new TrafficCounters(1, 1), s_start);
        meter.Add(new TrafficCounters(2, 2), s_start.AddSeconds(1));

        meter.Reset();

        Assert.Null(meter.Total);
        Assert.Empty(meter.Samples);
        Assert.Equal(default, meter.Current);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 100)]
    [InlineData(100, 100)]
    [InlineData(150, 200)]
    [InlineData(450, 500)]
    [InlineData(800, 1000)]
    [InlineData(1500, 2000)]
    [InlineData(12_000, 20_000)]
    public void ScaleIsRoundedUpToOneTwoFive(double peakKb, double expectedKb) =>
        Assert.Equal(expectedKb * 1024, TrafficMeter.NiceMaximum([new TrafficSample(0, peakKb * 1024), new TrafficSample(peakKb * 512, 0)]));
}
