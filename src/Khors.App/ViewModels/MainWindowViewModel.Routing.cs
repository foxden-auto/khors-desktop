using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Core.Profiles;
using Khors.Core.Routing;
using Khors.Engines.Connection;
using Khors.Engines.Geo;

namespace Khors.App.ViewModels;

/// <summary>Вид правила в списке выбора (текст — из ресурсов).</summary>
public sealed record RuleMatchOption(RuleMatch Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Действие правила в списке выбора.</summary>
public sealed record RuleActionOption(RuleAction Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Строка своего правила в настройках.</summary>
public sealed record RoutingRuleItem(int Index, RoutingRule Rule, string MatchText, string ActionText, bool CanMoveUp, bool CanMoveDown);

/// <summary>
/// Маршрутизация (ROADMAP 3.5): пресет, обход локальной сети и свои правила. Сохраняется сразу, действует со следующего
/// подключения; если подключено — предлагается переподключиться.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public IReadOnlyList<RuleMatchOption> RuleMatchOptions { get; } =
        [.. Enum.GetValues<RuleMatch>().Select(m => new RuleMatchOption(m, Localizer.Get($"RuleMatch_{m}")))];

    public IReadOnlyList<RuleActionOption> RuleActionOptions { get; } =
        [.. Enum.GetValues<RuleAction>().Select(a => new RuleActionOption(a, Localizer.Get($"RuleAction_{a}")))];

    public ObservableCollection<RoutingRuleItem> RoutingRules { get; } = [];

    public bool HasRoutingRules => RoutingRules.Count > 0;

    [ObservableProperty]
    public partial bool IsRoutingAll { get; set; }

    [ObservableProperty]
    public partial bool IsRoutingRussia { get; set; }

    [ObservableProperty]
    public partial bool IsRoutingApps { get; set; }

    [ObservableProperty]
    public partial bool RoutingBypassLan { get; set; }

    /// <summary>Режим TUN: IPv6 через сервер (ROADMAP 4.2); выключено — IPv6 отклоняется, программы идут по IPv4.</summary>
    [ObservableProperty]
    public partial bool RoutingTunIpv6 { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewRulePlaceholder))]
    public partial RuleMatchOption NewRuleMatch { get; set; } = new(RuleMatch.Domain, Localizer.Get("RuleMatch_Domain"));

    [ObservableProperty]
    public partial string? NewRuleValue { get; set; }

    [ObservableProperty]
    public partial RuleActionOption NewRuleAction { get; set; } = new(RuleAction.Direct, Localizer.Get("RuleAction_Direct"));

    /// <summary>Почему правило не добавлено; <c>null</c> — нет ошибки.</summary>
    [ObservableProperty]
    public partial string? NewRuleError { get; set; }

    /// <summary>Правило добавлено, но пока не действует (гео-базы не загружены).</summary>
    [ObservableProperty]
    public partial string? NewRuleWarning { get; set; }

    /// <summary>Коды категорий базы окна для выбранного вида правила (geosite/geoip), иначе пусто.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> GeoSuggestions { get; set; } = [];

    /// <summary>Маршрутизация изменена во время подключения — действует после переподключения.</summary>
    [ObservableProperty]
    public partial bool RoutingNeedsReconnect { get; set; }

    /// <summary>На странице «Подключение»: часть правил не действует, потому что категорий нет в гео-базе.</summary>
    [ObservableProperty]
    public partial string? RoutingWarning { get; set; }

    public string NewRulePlaceholder => Localizer.Get($"RulePlaceholder_{NewRuleMatch.Value}");

    private void LoadRoutingSetting()
    {
        var routing = _settings.Current.Routing;
        IsRoutingAll = routing.Preset == RoutingPreset.AllTraffic;
        IsRoutingRussia = routing.Preset == RoutingPreset.BypassRussia;
        IsRoutingApps = routing.Preset == RoutingPreset.SelectedApps;
        RoutingBypassLan = routing.BypassLan;
        RoutingTunIpv6 = routing.TunIpv6;
        ShowRoutingRules(routing.Rules);
    }

    private void ShowRoutingRules(EquatableArray<RoutingRule> rules)
    {
        RoutingRules.Clear();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            RoutingRules.Add(new RoutingRuleItem(
                i,
                rule,
                Localizer.Get($"RuleMatch_{rule.Match}"),
                Localizer.Get($"RuleAction_{rule.Action}"),
                CanMoveUp: i > 0,
                CanMoveDown: i < rules.Count - 1));
        }

        OnPropertyChanged(nameof(HasRoutingRules));
    }

    partial void OnIsRoutingAllChanged(bool value) => SetPreset(value, RoutingPreset.AllTraffic);

    partial void OnIsRoutingRussiaChanged(bool value) => SetPreset(value, RoutingPreset.BypassRussia);

    partial void OnIsRoutingAppsChanged(bool value) => SetPreset(value, RoutingPreset.SelectedApps);

    private void SetPreset(bool selected, RoutingPreset preset)
    {
        if (selected)
        {
            SaveRouting(r => r with { Preset = preset });
        }
    }

    partial void OnRoutingBypassLanChanged(bool value) => SaveRouting(r => r with { BypassLan = value });

    partial void OnRoutingTunIpv6Changed(bool value) => SaveRouting(r => r with { TunIpv6 = value });

    partial void OnNewRuleMatchChanged(RuleMatchOption value)
    {
        NewRuleError = null;
        NewRuleWarning = null;
        _ = LoadGeoSuggestionsAsync(value.Value);
    }

    partial void OnNewRuleValueChanged(string? value) => NewRuleError = null;

    /// <summary>Подсказки читаются из файла базы — не в потоке окна.</summary>
    private async Task LoadGeoSuggestionsAsync(RuleMatch match)
    {
        GeoSuggestions = [];
        if (match is not (RuleMatch.GeoSite or RuleMatch.GeoIp))
        {
            return;
        }

        var kind = match == RuleMatch.GeoSite ? GeoDatabaseKind.Site : GeoDatabaseKind.Ip;
        var codes = await Task.Run(() => _geo.ListCategories(kind)).ConfigureAwait(true);
        if (NewRuleMatch.Value == match)
        {
            GeoSuggestions = codes;
        }
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var match = NewRuleMatch.Value;
        var normalized = RoutingRuleValidator.Normalize(match, NewRuleValue);
        NewRuleWarning = null;
        if (normalized.Value is not { } value)
        {
            // Пустое поле — ещё не введено: без красного текста.
            NewRuleError = normalized.Error == RoutingRuleError.Empty ? null : Localizer.Get($"RoutingError_{normalized.Error}");
            return;
        }

        var rule = new RoutingRule(match, value, NewRuleAction.Value);
        if (_settings.Current.Routing.Rules.Contains(rule))
        {
            NewRuleError = Localizer.Get("RoutingError_Duplicate");
            return;
        }

        // Опечатку в коде категории видно сразу, если база загружена; без базы правило сохраняется и заработает после загрузки.
        if (match is RuleMatch.GeoSite or RuleMatch.GeoIp)
        {
            var kind = match == RuleMatch.GeoSite ? GeoDatabaseKind.Site : GeoDatabaseKind.Ip;
            var code = match == RuleMatch.GeoSite ? RoutingRuleValidator.GeoSiteBaseCode(value) : value.TrimStart('!');
            var codes = await Task.Run(() => _geo.ListCategories(kind)).ConfigureAwait(true);
            if (codes.Count == 0)
            {
                NewRuleWarning = Localizer.Get("RoutingNoGeoWarning");
            }
            else if (!codes.Contains(code, StringComparer.Ordinal))
            {
                NewRuleError = Localizer.Format("RoutingError_UnknownCategoryFormat", code);
                return;
            }
        }

        NewRuleError = null;
        NewRuleValue = null;
        SaveRouting(r => r with { Rules = new EquatableArray<RoutingRule>([.. r.Rules, rule]) });
    }

    [RelayCommand]
    private void MoveRuleUp(RoutingRuleItem item) => MoveRule(item, -1);

    [RelayCommand]
    private void MoveRuleDown(RoutingRuleItem item) => MoveRule(item, +1);

    private void MoveRule(RoutingRuleItem item, int offset)
    {
        var rules = _settings.Current.Routing.Rules.ToList();
        var target = item.Index + offset;
        if (item.Index >= rules.Count || target < 0 || target >= rules.Count || rules[item.Index] != item.Rule)
        {
            return;
        }

        (rules[item.Index], rules[target]) = (rules[target], rules[item.Index]);
        SaveRouting(r => r with { Rules = new EquatableArray<RoutingRule>(rules) });
    }

    [RelayCommand]
    private void RemoveRule(RoutingRuleItem item)
    {
        var rules = _settings.Current.Routing.Rules.ToList();
        if (item.Index < rules.Count && rules[item.Index] == item.Rule)
        {
            rules.RemoveAt(item.Index);
            SaveRouting(r => r with { Rules = new EquatableArray<RoutingRule>(rules) });
        }
    }

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        RoutingNeedsReconnect = false;
        if (_connection.Status is { State: ConnectionState.Connected, Profile: { } profile })
        {
            await _connection.ConnectAsync(profile, CoreStartPreferences.From(_settings.Current)).ConfigureAwait(true);
        }
    }

    private void SaveRouting(Func<RoutingSettings, RoutingSettings> change)
    {
        var current = _settings.Current.Routing;
        var updated = change(current);
        if (updated == current)
        {
            return;
        }

        _settings.Update(s => s with { Routing = updated });
        ShowRoutingRules(updated.Rules);
        if (State == ConnectionState.Connected)
        {
            RoutingNeedsReconnect = true;
        }
    }

    /// <summary>Из <see cref="ApplyStatus"/>: предупреждение о категориях и сброс «нужно переподключиться».</summary>
    private void ApplyRoutingStatus(ConnectionStatus status)
    {
        RoutingWarning = status is { State: ConnectionState.Connected, MissingGeo.Count: > 0 }
            ? Localizer.Format("RoutingMissingGeoFormat", string.Join(", ", status.MissingGeo))
            : null;
        if (status.State != ConnectionState.Connected)
        {
            RoutingNeedsReconnect = false;
        }
    }
}
