using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Engines.Connection;

namespace Khors.App.ViewModels;

/// <summary>Отправлено/получено и график скорости: раз в секунду — счётчики ядра текущего подключения.</summary>
public sealed partial class MainWindowViewModel
{
    private readonly TrafficMeter _meter = new();
    private int _trafficPolling;

    /// <summary>Отправлено с подключения; <c>null</c> — не подключено или ядро не отдаёт счётчики.</summary>
    [ObservableProperty]
    public partial string? SessionSent { get; set; }

    [ObservableProperty]
    public partial string? SessionReceived { get; set; }

    [ObservableProperty]
    public partial string RateUpText { get; set; } = Localizer.Rate(0);

    [ObservableProperty]
    public partial string RateDownText { get; set; } = Localizer.Rate(0);

    [ObservableProperty]
    public partial IReadOnlyList<TrafficSample> TrafficSamples { get; set; } = [];

    [ObservableProperty]
    public partial double ChartMaximum { get; set; } = TrafficMeter.NiceMaximum([]);

    [ObservableProperty]
    public partial string ChartMaximumText { get; set; } = Localizer.Rate(TrafficMeter.NiceMaximum([]));

    public int ChartCapacity => _meter.Capacity;

    private void OnSessionTick()
    {
        UpdateSessionTime();
        _ = PollTrafficAsync();
    }

    private async Task PollTrafficAsync()
    {
        if (State != ConnectionState.Connected || Interlocked.Exchange(ref _trafficPolling, 1) == 1)
        {
            return;
        }

        try
        {
            var counters = await _connection.ReadTrafficAsync().ConfigureAwait(true);
            if (counters is null || State != ConnectionState.Connected)
            {
                return;
            }

            _meter.Add(counters, DateTimeOffset.UtcNow);
            PublishTraffic();
        }
        finally
        {
            Interlocked.Exchange(ref _trafficPolling, 0);
        }
    }

    private void ResetTraffic()
    {
        _meter.Reset();
        PublishTraffic();
    }

    private void PublishTraffic()
    {
        SessionSent = _meter.Total is { } total ? Localizer.Bytes(total.Uplink) : null;
        SessionReceived = _meter.Total is { } received ? Localizer.Bytes(received.Downlink) : null;
        RateUpText = Localizer.Rate(_meter.Current.UpPerSecond);
        RateDownText = Localizer.Rate(_meter.Current.DownPerSecond);
        TrafficSamples = _meter.Samples;
        ChartMaximum = TrafficMeter.NiceMaximum(TrafficSamples);
        ChartMaximumText = Localizer.Rate(ChartMaximum);
    }
}
