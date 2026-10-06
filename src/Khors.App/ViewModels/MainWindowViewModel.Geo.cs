using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Engines.Geo;
using Khors.Ipc;

namespace Khors.App.ViewModels;

/// <summary>Гео-базы (ROADMAP 3.6): состояние файлов окна и службы, «Обновить сейчас», автообновление.</summary>
public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial string GeoSiteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GeoIpText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GeoServiceText { get; set; } = string.Empty;

    /// <summary>Почему последнее обновление окна не удалось; <c>null</c> — удалось или ещё не было.</summary>
    [ObservableProperty]
    public partial string? GeoErrorText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateGeoCommand))]
    public partial bool IsGeoUpdating { get; set; }

    [ObservableProperty]
    public partial bool GeoAutoUpdate { get; set; }

    private void InitGeo()
    {
        GeoAutoUpdate = _settings.Current.GeoAutoUpdate;
        _geo.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshGeo);
        RefreshGeo();
        _ = Task.Run(() => _geo.RefreshServiceAsync());
    }

    [RelayCommand(CanExecute = nameof(CanUpdateGeo))]
    private async Task UpdateGeoAsync()
    {
        IsGeoUpdating = true;
        try
        {
            await Task.Run(() => _geo.UpdateNowAsync()).ConfigureAwait(true);
        }
        finally
        {
            RefreshGeo();
        }
    }

    private bool CanUpdateGeo() => !IsGeoUpdating;

    partial void OnGeoAutoUpdateChanged(bool value)
    {
        if (value != _settings.Current.GeoAutoUpdate)
        {
            _settings.Update(s => s with { GeoAutoUpdate = value });
        }
    }

    private void RefreshGeo()
    {
        var state = _geo.Current;
        IsGeoUpdating = state.IsUpdating;
        GeoSiteText = DescribeFile(GeoDatabaseKind.Site, state.Local.FirstOrDefault(f => f.Kind == GeoDatabaseKind.Site));
        GeoIpText = DescribeFile(GeoDatabaseKind.Ip, state.Local.FirstOrDefault(f => f.Kind == GeoDatabaseKind.Ip));
        GeoErrorText = state.LocalErrors.Count == 0
            ? null
            : string.Join(" ", state.LocalErrors.Values.Distinct().Select(e => Localizer.Get($"GeoError_{e}")));
        GeoServiceText = DescribeService(state.Service);
    }

    private static string DescribeFile(GeoDatabaseKind kind, GeoFileStatus? file)
    {
        var name = Localizer.Get($"GeoFile_{kind}");
        return file is { Updated: { } updated }
            ? Localizer.Format("GeoFileFormat", name, updated.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Localizer.Bytes(file.Size))
            : Localizer.Format("GeoFileMissingFormat", name);
    }

    /// <summary>Копия службы одной строкой: самая старая дата или «не загружена», плюс ошибка, если была.</summary>
    private static string DescribeService(IReadOnlyList<IpcGeoFile>? files)
    {
        if (files is null)
        {
            return Localizer.Get("GeoServiceUnavailable");
        }

        var state = files.All(f => f.Updated is not null)
            ? Localizer.Format("GeoServiceUpdatedFormat", files.Min(f => f.Updated!.Value).ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Localizer.Get("GeoServiceMissing");
        var error = files.Select(f => f.Error).FirstOrDefault(e => e is not null);
        return error is null ? state : $"{state} {Localizer.Get($"GeoError_{error}")}";
    }
}
