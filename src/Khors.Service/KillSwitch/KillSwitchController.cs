using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Khors.Ipc;
using Khors.Platform;
using Microsoft.Extensions.Logging;

namespace Khors.Service.KillSwitch;

/// <summary>Настройка Kill Switch службы (<c>%ProgramData%\KHORS\killswitch.json</c>).</summary>
public sealed record KillSwitchSettings(IpcKillSwitchMode Mode = IpcKillSwitchMode.Off, bool AllowLocalNetwork = true);

/// <summary>
/// Kill Switch в службе (ROADMAP 4.1, docs/SPEC.md, 4.6); действует в режиме TUN. Разрешено: loopback, адаптер TUN,
/// ядра из каталога службы (они сами решают, что идёт мимо туннеля — обход для России, прямой DNS), DHCP и
/// локальная сеть (по настройке). «Пока подключено»: блокировка ставится до запуска ядер и снимается только явным
/// отключением (или неудачным запуском — подключения не было); падение ядра или окна её не снимает, пока окно не
/// подключится снова или не снимет блокировку (<see cref="Release"/>). Фильтры временные — исчезают, если служба
/// завершится. «Постоянно»: постоянные фильтры, без туннеля трафик заблокирован всегда, в том числе при загрузке.
/// </summary>
public sealed partial class KillSwitchController
{
    public const string TunnelInterface = "KHORS";

    private readonly IKillSwitch? _killSwitch;
    private readonly string _settingsFile;
    private readonly IReadOnlyList<string> _applications;
    private readonly ILogger<KillSwitchController> _logger;
    private readonly Lock _lock = new();
    private KillSwitchSettings _settings;
    private bool _engaged;
    private bool _engagedBeforeStart;
    private bool _tunnelUp;
    private bool _tunnelIpv6;
    private bool _active;
    private string? _error;

    /// <param name="killSwitch"><c>null</c> — платформа без Kill Switch: настройка сохраняется, фильтров нет.</param>
    /// <param name="applications">Полные пути ядер в каталоге службы.</param>
    public KillSwitchController(IKillSwitch? killSwitch, string settingsFile, IReadOnlyList<string> applications, ILogger<KillSwitchController> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsFile);
        ArgumentNullException.ThrowIfNull(applications);
        ArgumentNullException.ThrowIfNull(logger);
        _killSwitch = killSwitch;
        _settingsFile = settingsFile;
        _applications = applications;
        _logger = logger;
        _settings = Load(settingsFile);
    }

    /// <summary>
    /// При запуске службы: «Постоянно» — фильтры на месте (без туннеля); иначе — постоянные фильтры, оставшиеся от
    /// прежней настройки, снимаются (CLAUDE.md, правило 9).
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            if (_settings.Mode != IpcKillSwitchMode.Always && Try(() => _killSwitch?.HasPersistentFilters() == true))
            {
                Try(() => _killSwitch!.Remove());
            }

            Refresh();
        }
    }

    public KillSwitchStatusResponse Status()
    {
        lock (_lock)
        {
            return new KillSwitchStatusResponse(_settings.Mode, _settings.AllowLocalNetwork, _active, _active && !_tunnelUp, _error);
        }
    }

    public KillSwitchStatusResponse Set(IpcKillSwitchMode mode, bool allowLocalNetwork)
    {
        lock (_lock)
        {
            _settings = new KillSwitchSettings(mode, allowLocalNetwork);
            try
            {
                Save(_settingsFile, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Действует до перезапуска службы; причина видна окну.
                _error = ex.Message;
                LogFailed(ex.Message);
            }

            if (mode == IpcKillSwitchMode.WhileConnected && _tunnelUp)
            {
                // Включили во время подключения — защищаем это подключение.
                _engaged = true;
            }

            Refresh();
        }

        return Status();
    }

    /// <summary>Снимает блокировку «пока подключено» без туннеля (после падения ядра или окна).</summary>
    public KillSwitchStatusResponse Release()
    {
        lock (_lock)
        {
            if (!_tunnelUp)
            {
                _engaged = false;
                Refresh();
            }
        }

        return Status();
    }

    /// <summary>
    /// «Сбросить сетевые настройки KHORS» (ROADMAP 4.4): режим — «Выключен» (иначе «Постоянно» вернуло бы блокировку
    /// при следующем запуске службы), все фильтры и объекты WFP KHORS снимаются, в том числе оставшиеся от прошлых запусков.
    /// </summary>
    /// <returns><c>null</c> — фильтров не было; иначе — сняты ли они.</returns>
    public bool? Reset()
    {
        lock (_lock)
        {
            _settings = _settings with { Mode = IpcKillSwitchMode.Off };
            try
            {
                Save(_settingsFile, _settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error = ex.Message;
                LogFailed(ex.Message);
            }

            _engaged = false;
            _tunnelUp = false;
            if (_killSwitch is null)
            {
                return null;
            }

            var hadFilters = _active || Try(_killSwitch.HasPersistentFilters);

            // Снимаем всегда: временные фильтры прошлого процесса службы WFP уже убрал, а свои объекты — нет.
            if (!Try(_killSwitch.Remove))
            {
                return false;
            }

            _active = false;
            LogRemoved();
            return hadFilters ? true : null;
        }
    }

    /// <summary>До запуска ядер TUN: блокировка (адаптера ещё нет). <c>false</c> — фильтры не поставились, запускать нельзя.</summary>
    public bool TunStarting()
    {
        lock (_lock)
        {
            _tunnelUp = false;
            _engagedBeforeStart = _engaged;
            _engaged = _settings.Mode != IpcKillSwitchMode.Off;
            return Refresh();
        }
    }

    /// <summary>Адаптер TUN поднят — трафик через него разрешён.</summary>
    /// <param name="tunnelIpv6">IPv6 идёт через сервер; иначе IPv6 через адаптер тоже блокируется.</param>
    public bool TunStarted(bool tunnelIpv6 = false)
    {
        lock (_lock)
        {
            _tunnelUp = true;
            _tunnelIpv6 = tunnelIpv6;
            return Refresh();
        }
    }

    /// <summary>
    /// TUN не запустился: подключения не было — блокировка «пока подключено» снимается. Если она стояла и до запуска
    /// (подключение оборвалось, окно переподключается — ROADMAP 4.3), она остаётся.
    /// </summary>
    public void TunStartFailed()
    {
        lock (_lock)
        {
            _tunnelUp = false;
            _engaged = _engagedBeforeStart;
            Refresh();
        }
    }

    /// <param name="byUser">Отключение пользователем — снимает блокировку «пока подключено»; закрытое окно или падение ядра — нет.</param>
    public void TunStopped(bool byUser)
    {
        lock (_lock)
        {
            _tunnelUp = false;
            if (byUser)
            {
                _engaged = false;
            }

            Refresh();
        }
    }

    /// <summary>Приводит фильтры к состоянию; <c>false</c> — WFP отказал (ошибка — в <see cref="Status"/>).</summary>
    private bool Refresh()
    {
        if (_killSwitch is null)
        {
            return true;
        }

        var wanted = _settings.Mode switch
        {
            IpcKillSwitchMode.Always => true,
            IpcKillSwitchMode.WhileConnected => _engaged,
            _ => false,
        };

        if (!wanted)
        {
            if (_active && Try(_killSwitch.Remove))
            {
                _active = false;
                LogRemoved();
            }

            return !_active;
        }

        var policy = new KillSwitchPolicy(
            Persistent: _settings.Mode == IpcKillSwitchMode.Always,
            _settings.AllowLocalNetwork,
            _applications,
            _tunnelUp ? TunnelInterface : null,
            _tunnelIpv6);
        if (!Try(() => _killSwitch.Apply(policy)))
        {
            return false;
        }

        // Переход «постоянно» → «пока подключено»: Apply заменил все наши фильтры временными.
        _active = true;
        LogApplied(_settings.Mode, _tunnelUp);
        return true;
    }

    private bool Try(Action action) => Try(() =>
    {
        action();
        return true;
    });

    private bool Try(Func<bool> action)
    {
        try
        {
            var result = action();
            _error = null;
            return result;
        }
        catch (Win32Exception ex)
        {
            _error = ex.Message;
            LogFailed(ex.Message);
            return false;
        }
    }

    private static KillSwitchSettings Load(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), KillSwitchJsonContext.Default.KillSwitchSettings) ?? new KillSwitchSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new KillSwitchSettings();
        }
    }

    private static void Save(string file, KillSwitchSettings settings)
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, KillSwitchJsonContext.Default.KillSwitchSettings));
        File.Move(temporary, file, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Kill switch applied: mode {Mode}, tunnel up {TunnelUp}")]
    private partial void LogApplied(IpcKillSwitchMode mode, bool tunnelUp);

    [LoggerMessage(Level = LogLevel.Information, Message = "Kill switch removed")]
    private partial void LogRemoved();

    [LoggerMessage(Level = LogLevel.Error, Message = "Kill switch failed: {Error}")]
    private partial void LogFailed(string error);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KillSwitchSettings))]
internal sealed partial class KillSwitchJsonContext : JsonSerializerContext;
