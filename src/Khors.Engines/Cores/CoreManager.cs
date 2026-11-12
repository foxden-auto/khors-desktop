using Khors.Ipc;

namespace Khors.Engines.Cores;

/// <summary>Ядра службы по IPC; <c>null</c> — служба не отвечает, не установлена или старая.</summary>
public interface IServiceCoresClient
{
    Task<CoresStatusResponse?> GetStatusAsync(CancellationToken cancellationToken);

    Task<CoresStatusResponse?> UpdateAsync(CoreKind kind, string version, CancellationToken cancellationToken);

    Task<CoresStatusResponse?> RollbackAsync(CoreKind kind, CancellationToken cancellationToken);
}

/// <summary>Состояние одного ядра для окна.</summary>
/// <param name="Local">Копия окна (режим «Системный прокси», тест задержки).</param>
/// <param name="Service">Копия службы (режим TUN); <c>null</c> — служба недоступна.</param>
/// <param name="Available">Последний релиз серии новее копии окна или службы (после «Проверить обновления»).</param>
/// <param name="NewerSeries">Есть версия новой серии — нужна новая версия KHORS.</param>
/// <param name="CheckError">Причина неудачной проверки.</param>
public sealed record CoreState(
    CoreKind Kind,
    CoreVersionStatus Local,
    IpcCoreState? Service,
    CoreRelease? Available,
    string? NewerSeries,
    CoreUpdateError? CheckError);

/// <summary>Итог обновления или отката одного ядра в окне и в службе.</summary>
/// <param name="ServiceError">Код ошибки службы (<c>CoreUpdateError</c> или <c>NothingToRollback</c>), <c>Unavailable</c> — служба не ответила; <c>null</c> — успех или служба не участвовала.</param>
public sealed record CoreActionOutcome(CoreKind Kind, string? Version, CoreUpdateError? LocalError, string? LocalDetail, string? ServiceError, string? ServiceDetail);

/// <summary>
/// Ядра окна и службы (ROADMAP 5.2): «Проверить обновления», «Обновить» (сначала окно, затем служба — она скачивает и
/// проверяет свою копию сама), «Откатить». Только по команде пользователя: фоновых проверок нет (CLAUDE.md, правило 6).
/// </summary>
public sealed class CoreManager(CoreUpdater local, IServiceCoresClient? service)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<CoreKind, CoreCheckOutcome> _checks = [];
    private IReadOnlyList<IpcCoreState>? _service;

    public IReadOnlyList<CoreState> Current
    {
        get
        {
            lock (_lock)
            {
                return [.. CoreCatalog.All.Select(core =>
                {
                    var localStatus = local.Store.Status(core.Kind);
                    var serviceStatus = _service?.FirstOrDefault(s => s.Core == ToIpc(core.Kind));
                    var check = _checks.GetValueOrDefault(core.Kind);
                    var latest = check?.Choice?.Latest;
                    var newer = latest is not null
                        && (CoreVersion.Compare(latest.Version, localStatus.Version) > 0
                            || (serviceStatus is not null && CoreVersion.Compare(latest.Version, serviceStatus.Version) > 0));
                    return new CoreState(core.Kind, localStatus, serviceStatus, newer ? latest : null, check?.Choice?.NewerSeries, check?.Error);
                })];
            }
        }
    }

    /// <summary>Версии службы без сети.</summary>
    public async Task RefreshServiceAsync(CancellationToken cancellationToken = default)
    {
        if (service is not null)
        {
            SetService(await service.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Запрос к GitHub для каждого ядра и обновлённые версии службы.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        foreach (var core in CoreCatalog.All)
        {
            var outcome = await local.CheckAsync(core.Kind, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _checks[core.Kind] = outcome;
            }
        }

        await RefreshServiceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Обновляет ядра, для которых есть релиз новее: окно, затем служба (если её копия старее).</summary>
    public async Task<IReadOnlyList<CoreActionOutcome>> UpdateAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = new List<CoreActionOutcome>();
        foreach (var state in Current.Where(s => s.Available is not null))
        {
            var release = state.Available!;
            CoreUpdateOutcome? localOutcome = null;
            if (CoreVersion.Compare(release.Version, state.Local.Version) > 0)
            {
                localOutcome = await local.InstallAsync(release, cancellationToken).ConfigureAwait(false);
            }

            string? serviceError = null;
            string? serviceDetail = null;
            if (service is not null && state.Service is { } serviceState && CoreVersion.Compare(release.Version, serviceState.Version) > 0
                && localOutcome?.Error is not CoreUpdateError.Incompatible)
            {
                var answer = await service.UpdateAsync(state.Kind, release.Version, cancellationToken).ConfigureAwait(false);
                SetService(answer);
                serviceError = answer is null ? "Unavailable" : answer.Error;
                serviceDetail = answer?.Detail;
            }

            outcomes.Add(new CoreActionOutcome(state.Kind, release.Version, localOutcome?.Error, localOutcome?.Detail, serviceError, serviceDetail));
        }

        await RefreshServiceAsync(cancellationToken).ConfigureAwait(false);
        return outcomes;
    }

    /// <summary>Откат ядра в окне и в службе — там, где есть на что откатываться.</summary>
    public async Task<CoreActionOutcome> RollbackAsync(CoreKind kind, CancellationToken cancellationToken = default)
    {
        var state = Current.First(s => s.Kind == kind);
        CoreUpdateError? localError = null;
        if (state.Local.PreviousVersion is not null && !local.Rollback(kind))
        {
            localError = CoreUpdateError.WriteFailed;
        }

        string? serviceError = null;
        if (service is not null && state.Service is { PreviousVersion: not null })
        {
            var answer = await service.RollbackAsync(kind, cancellationToken).ConfigureAwait(false);
            SetService(answer);
            serviceError = answer is null ? "Unavailable" : answer.Error;
        }

        return new CoreActionOutcome(kind, null, localError, null, serviceError, null);
    }

    private void SetService(CoresStatusResponse? status)
    {
        lock (_lock)
        {
            _service = status?.Cores;
        }
    }

    private static IpcCore ToIpc(CoreKind kind) => kind switch
    {
        CoreKind.Xray => IpcCore.Xray,
        CoreKind.SingBox => IpcCore.SingBox,
        _ => IpcCore.SingBoxExtended,
    };
}

/// <summary>Ядра службы по IPC: отдельное соединение на запрос.</summary>
public sealed class ServiceCoresClient(IIpcClientTransport transport, string clientVersion) : IServiceCoresClient
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(3);

    // Служба скачивает архив ядра (до ~40 МБ) и проверяет его — с запасом на медленную сеть.
    private static readonly TimeSpan s_updateTimeout = TimeSpan.FromMinutes(15);

    public Task<CoresStatusResponse?> GetStatusAsync(CancellationToken cancellationToken) =>
        RequestAsync(new GetCoresRequest(), s_connectTimeout, cancellationToken);

    public Task<CoresStatusResponse?> UpdateAsync(CoreKind kind, string version, CancellationToken cancellationToken) =>
        RequestAsync(new UpdateCoreRequest(ToIpc(kind), version), s_updateTimeout, cancellationToken);

    public Task<CoresStatusResponse?> RollbackAsync(CoreKind kind, CancellationToken cancellationToken) =>
        RequestAsync(new RollbackCoreRequest(ToIpc(kind)), s_connectTimeout, cancellationToken);

    private static IpcCore ToIpc(CoreKind kind) => kind switch
    {
        CoreKind.Xray => IpcCore.Xray,
        CoreKind.SingBox => IpcCore.SingBox,
        _ => IpcCore.SingBoxExtended,
    };

    private async Task<CoresStatusResponse?> RequestAsync(IpcPayload request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await using var client = await IpcClient.ConnectAsync(transport, clientVersion, s_connectTimeout, limit.Token).ConfigureAwait(false);
            return await client.RequestAsync(request, limit.Token).ConfigureAwait(false) as CoresStatusResponse;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException
            or IpcDisconnectedException or IpcProtocolException or IpcVersionMismatchException)
        {
            return null;
        }
    }
}
