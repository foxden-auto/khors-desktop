using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Core.Dns;

namespace Khors.App.ViewModels;

/// <summary>Вариант удалённого DNS в настройках: пресет или «Свой адрес» (<see cref="Preset"/> = <c>null</c>).</summary>
public sealed record DnsOption(DnsPreset? Preset, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Удалённый DNS для режима TUN и WireGuard (ROADMAP 3.4). Применяется при следующем подключении.</summary>
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<DnsOption> DnsOptions { get; } =
    [
        .. DnsPresets.All.Select(p => new DnsOption(p, Localizer.Format("DnsPresetFormat", p.Provider, Localizer.Get($"DnsKind_{p.Server.Type}"), p.Server.Host))),
        new DnsOption(null, Localizer.Get("DnsCustomOption")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomDns))]
    public partial DnsOption? SelectedDnsOption { get; set; }

    [ObservableProperty]
    public partial string CustomDnsText { get; set; } = string.Empty;

    /// <summary>Почему свой адрес не принят; <c>null</c> — адрес верный (или выбран пресет).</summary>
    [ObservableProperty]
    public partial string? CustomDnsError { get; set; }

    public bool IsCustomDns => SelectedDnsOption is { Preset: null };

    /// <summary>Выбор из сохранённой настройки: адрес пресета — пресет, иначе «Свой адрес» с этим текстом.</summary>
    private void LoadDnsSetting()
    {
        var server = DnsPresets.ServerOrDefault(_settings.Current.RemoteDns);
        var preset = DnsPresets.Find(server);
        CustomDnsText = preset is null ? server.ToString() : string.Empty;
        SelectedDnsOption = DnsOptions.First(o => o.Preset == preset);
    }

    partial void OnSelectedDnsOptionChanged(DnsOption? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Preset is { } preset)
        {
            CustomDnsError = null;
            SaveRemoteDns(preset.Server);
        }
        else
        {
            ApplyCustomDns(CustomDnsText);
        }
    }

    partial void OnCustomDnsTextChanged(string value)
    {
        if (IsCustomDns)
        {
            ApplyCustomDns(value);
        }
    }

    /// <summary>Верный адрес сохраняется сразу; неверный — подсказка, в настройках остаётся прежний.</summary>
    private void ApplyCustomDns(string text)
    {
        var parsed = DnsServer.Parse(text);
        if (!parsed.IsSuccess)
        {
            // Пустое поле — ещё не введено: без красного текста.
            CustomDnsError = parsed.Error == DnsServerParseError.Empty ? null : Localizer.Get($"DnsError_{parsed.Error}");
            return;
        }

        CustomDnsError = null;
        SaveRemoteDns(parsed.Server);
    }

    private void SaveRemoteDns(DnsServer server)
    {
        var text = server.ToString();
        if (text != _settings.Current.RemoteDns)
        {
            _settings.Update(s => s with { RemoteDns = text });
        }
    }
}
