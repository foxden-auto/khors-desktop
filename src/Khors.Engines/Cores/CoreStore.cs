using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Engines.Cores;

/// <summary>Какая версия ядра работает и на какую можно откатиться.</summary>
/// <param name="Active">Версия из хранилища; <c>null</c> — ядро из поставки.</param>
/// <param name="Previous">Версия для отката из хранилища; <c>null</c> при <see cref="CanRollback"/> — поставка.</param>
public sealed record CoreSlot(string? Active = null, bool CanRollback = false, string? Previous = null);

/// <summary>Состояние ядра для окна и службы.</summary>
/// <param name="Version">Работающая версия (из хранилища или поставки).</param>
/// <param name="Shipped">Работает ядро из поставки.</param>
/// <param name="PreviousVersion">На какую версию откатится «Откатить»; <c>null</c> — откатываться некуда.</param>
public sealed record CoreVersionStatus(CoreKind Kind, string Version, bool Shipped, string? PreviousVersion);

/// <summary>
/// Обновлённые ядра (ROADMAP 5.2): <c>&lt;каталог&gt;/&lt;ядро&gt;/&lt;версия&gt;/</c> — исполняемый файл и лицензия,
/// выбор — в <c>cores.json</c>. Окно — в данных пользователя, служба — в своём каталоге, куда пишет только администратор
/// (CLAUDE.md, правило 8). Хранятся работающая и предыдущая версии; откат возвращает предыдущую или поставку.
/// Каталог версии создаётся целиком во временном каталоге и переименовывается одной операцией.
/// </summary>
public sealed class CoreStore
{
    private const string StateFile = "cores.json";
    private readonly Lock _lock = new();

    public CoreStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>Исполняемый файл активной версии из хранилища; <c>null</c> — работает поставка (или файла нет).</summary>
    public string? ActiveExecutable(CoreKind kind)
    {
        var core = CoreCatalog.Get(kind);
        if (Slot(kind).Active is not { } version)
        {
            return null;
        }

        var path = ExecutablePath(core, version);
        return File.Exists(path) ? path : null;
    }

    public CoreSlot Slot(CoreKind kind)
    {
        lock (_lock)
        {
            return Load().TryGetValue(CoreCatalog.Get(kind).Directory, out var slot) ? slot : new CoreSlot();
        }
    }

    public CoreVersionStatus Status(CoreKind kind)
    {
        var core = CoreCatalog.Get(kind);
        var slot = Slot(kind);
        var active = ActiveExecutable(kind) is not null ? slot.Active : null;
        return new CoreVersionStatus(
            kind,
            active ?? core.ShippedVersion,
            active is null,
            slot.CanRollback ? slot.Previous ?? core.ShippedVersion : null);
    }

    /// <summary>
    /// Кладёт файлы версии (исполняемый файл и лицензию) в хранилище, не включая её. Возвращает путь исполняемого файла.
    /// </summary>
    public string Install(CoreKind kind, string version, byte[] executable, byte[] license)
    {
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(license);
        var core = CoreCatalog.Get(kind);
        var target = VersionDirectory(core, version);
        var temporary = target + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        System.IO.Directory.CreateDirectory(temporary);
        try
        {
            File.WriteAllBytes(Path.Combine(temporary, core.Executable), executable);
            File.WriteAllBytes(Path.Combine(temporary, "LICENSE"), license);
            lock (_lock)
            {
                if (System.IO.Directory.Exists(target))
                {
                    // Та же версия уже лежит (прошлая попытка) — заменяем: её файлы могли быть неполными.
                    System.IO.Directory.Delete(target, recursive: true);
                }

                System.IO.Directory.Move(temporary, target);
            }
        }
        finally
        {
            if (System.IO.Directory.Exists(temporary))
            {
                System.IO.Directory.Delete(temporary, recursive: true);
            }
        }

        return ExecutablePath(core, version);
    }

    /// <summary>Включает установленную версию; прежняя становится версией для отката. Лишние версии удаляются.</summary>
    public void Activate(CoreKind kind, string version)
    {
        var core = CoreCatalog.Get(kind);
        if (!File.Exists(ExecutablePath(core, version)))
        {
            throw new FileNotFoundException("Core version is not installed.", ExecutablePath(core, version));
        }

        lock (_lock)
        {
            var state = Load();
            var current = state.TryGetValue(core.Directory, out var slot) ? slot : new CoreSlot();
            state[core.Directory] = current.Active == version ? current : new CoreSlot(version, CanRollback: true, current.Active);
            Save(state);
        }

        Prune(core);
    }

    /// <summary>Отказывается от удаляемой версии (её не удалось проверить): каталог удаляется, выбор не меняется.</summary>
    public void Discard(CoreKind kind, string version)
    {
        var core = CoreCatalog.Get(kind);
        var slot = Slot(kind);
        if (slot.Active != version && slot.Previous != version)
        {
            TryDelete(VersionDirectory(core, version));
        }
    }

    /// <summary>Возвращает предыдущую версию (или поставку). <c>false</c> — откатываться некуда.</summary>
    public bool Rollback(CoreKind kind)
    {
        var core = CoreCatalog.Get(kind);
        lock (_lock)
        {
            var state = Load();
            if (!state.TryGetValue(core.Directory, out var slot) || !slot.CanRollback)
            {
                return false;
            }

            state[core.Directory] = new CoreSlot(slot.Previous);
            Save(state);
        }

        Prune(core);
        return true;
    }

    private string ExecutablePath(CoreInfo core, string version) => Path.Combine(VersionDirectory(core, version), core.Executable);

    private string VersionDirectory(CoreInfo core, string version)
    {
        // Версия — имя каталога: только числа и точки, без «..» и разделителей.
        if (!CoreVersion.IsValid(version))
        {
            throw new ArgumentException("Invalid core version.", nameof(version));
        }

        return Path.Combine(Directory, core.Directory, version);
    }

    /// <summary>Удаляет версии, кроме работающей и предыдущей. Занятые (ядро ещё работает) — в следующий раз.</summary>
    private void Prune(CoreInfo core)
    {
        var slot = Slot(core.Kind);
        var root = Path.Combine(Directory, core.Directory);
        if (!System.IO.Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name != slot.Active && name != slot.Previous)
            {
                TryDelete(directory);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл ядра занят работающим процессом — удалим при следующем обновлении или откате.
        }
    }

    private Dictionary<string, CoreSlot> Load()
    {
        try
        {
            var state = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Directory, StateFile)), CoreStoreJsonContext.Default.CoreStoreState);
            return state is { SchemaVersion: 1 } ? Sanitize(state.Cores) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    // Чужие значения в файле не должны превратиться в путь вне хранилища.
    private static Dictionary<string, CoreSlot> Sanitize(Dictionary<string, CoreSlot>? cores) =>
        (cores ?? []).Where(c => CoreCatalog.All.Any(k => k.Directory == c.Key))
            .ToDictionary(
                c => c.Key,
                c => new CoreSlot(
                    CoreVersion.IsValid(c.Value.Active) ? c.Value.Active : null,
                    c.Value.CanRollback && (c.Value.Previous is null || CoreVersion.IsValid(c.Value.Previous)),
                    CoreVersion.IsValid(c.Value.Previous) ? c.Value.Previous : null));

    private void Save(Dictionary<string, CoreSlot> cores)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, StateFile);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new CoreStoreState(1, cores), CoreStoreJsonContext.Default.CoreStoreState));
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed record CoreStoreState(int SchemaVersion, Dictionary<string, CoreSlot>? Cores);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CoreStoreState))]
internal sealed partial class CoreStoreJsonContext : JsonSerializerContext;
