using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.DevCli;
using Khors.Engines.Processes;
using Khors.Engines.Xray;
using Khors.Platform;

// Живая проверка: ссылка → профиль → Xray → тестовый запрос через прокси. Ссылка и секреты на экран не выводятся.
//   khors-devcli "<ссылка>"          ссылка аргументом
//   khors-devcli --file link.txt     ссылка из файла
//   khors-devcli                     link.txt рядом с программой или ввод с клавиатуры
// Ключи: --loglevel debug|info|warning|error   --socks <порт>   --http <порт>
//        --system-proxy  включить системный прокси Windows на HTTP-вход Xray (ROADMAP 1.5)
//        --no-wait       не ждать Enter перед выходом

var exitCode = await RunAsync(args);

// Окно, открытое двойным щелчком, Windows закрывает сразу после выхода — даём прочитать итог.
if (!Console.IsInputRedirected && !args.Contains("--no-wait"))
{
    Console.WriteLine();
    Console.WriteLine("Нажмите Enter, чтобы закрыть окно.");
    Console.ReadLine();
}

return exitCode;

static async Task<int> RunAsync(string[] args)
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine("KHORS DevCli — проверка подключения через Xray (ROADMAP 1.4). Ctrl+C — остановить.");
    Console.WriteLine();

    string? link = null;
    var logLevel = "warning";
    int? socksPort = 10808;
    int? httpPort = 10809;
    var useSystemProxy = false;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--file" when i + 1 < args.Length:
                link = File.ReadAllText(args[++i]);
                break;
            case "--loglevel" when i + 1 < args.Length:
                logLevel = args[++i];
                break;
            case "--socks" when i + 1 < args.Length:
                socksPort = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "--no-wait":
                break;
            case "--system-proxy":
                useSystemProxy = true;
                break;
            case "--http" when i + 1 < args.Length:
                httpPort = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                link = args[i];
                break;
        }
    }

    var linkFile = Path.Combine(AppContext.BaseDirectory, "link.txt");
    if (link is null && File.Exists(linkFile))
    {
        Console.WriteLine("Ссылка взята из link.txt рядом с программой.");
        link = File.ReadAllText(linkFile);
    }

    if (link is null)
    {
        Console.WriteLine("Вставьте ссылку (vless://, vmess://, trojan://, ss://) и нажмите Enter:");
        link = Console.ReadLine() ?? string.Empty;
    }

    var masker = new SecretMasker();

    var parsed = ShareLinkParser.Parse(link.Trim());
    if (!parsed.IsSuccess)
    {
        Console.WriteLine($"Ссылка не разобрана: {parsed.Error.Code} (поле: {parsed.Error.Field ?? "—"})");
        return 2;
    }

    var profile = parsed.Profile;
    Console.WriteLine($"Профиль: {profile}");
    Console.WriteLine($"Ссылка (замаскирована): {masker.MaskText(link.Trim())}");

    var issues = ProfileValidator.Validate(profile);
    foreach (var issue in issues)
    {
        Console.WriteLine($"  {issue.Severity}: {issue.Code} ({issue.Field})");
    }

    if (issues.Any(i => i.Severity == ProfileIssueSeverity.Error))
    {
        Console.WriteLine("В профиле есть ошибки — запуск невозможен.");
        return 2;
    }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    using var guard = PlatformComposition.CreateChildProcessGuard();

    ISystemProxy? systemProxy = null;
    if (useSystemProxy)
    {
        systemProxy = PlatformComposition.CreateSystemProxy();
        if (systemProxy is null)
        {
            Console.WriteLine("--system-proxy поддерживается только на Windows.");
            return 2;
        }

        // Следы прошлого аварийного завершения убираем до запуска.
        var recovery = systemProxy.RecoverAfterCrash();
        if (recovery != SystemProxyRecovery.NothingToRecover)
        {
            Console.WriteLine($"После прошлого сбоя: {recovery}.");
        }
    }

    // Закрытие окна (SIGHUP) и завершение (SIGTERM): у процесса есть несколько секунд — возвращаем прокси.
    void RestoreSystemProxy(string reason)
    {
        if (systemProxy?.Restore() == true)
        {
            Console.WriteLine($"Системный прокси Windows восстановлен ({reason}).");
        }
    }

    using var onClose = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => RestoreSystemProxy("закрытие окна"));
    using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => RestoreSystemProxy("завершение"));
    XraySession session;
    try
    {
        session = await XrayLauncher.StartAsync(
            profile,
            new XrayStartOptions { PreferredSocksPort = socksPort, PreferredHttpPort = httpPort, LogLevel = logLevel },
            masker,
            guard,
            stop.Token);
    }
    catch (CoreStartException ex)
    {
        Console.WriteLine($"Xray не запустился: {ex.Failure}. {ex.Message}");
        if (ex.ConfigError is { } configError)
        {
            Console.WriteLine($"  Причина: {configError.Code} (поле: {configError.Field})");
        }

        foreach (var line in ex.LogTail)
        {
            Console.WriteLine($"  | {line}");
        }

        return 1;
    }

    await using (session)
    {
        try
        {
            Console.WriteLine();
            Console.WriteLine($"Xray запущен (PID {session.Process.ProcessId}).");
            Console.WriteLine($"  SOCKS5: 127.0.0.1:{session.SocksPort}");
            Console.WriteLine($"  HTTP:   127.0.0.1:{session.HttpPort}");
            Console.WriteLine();

            foreach (var line in session.Process.Log.Snapshot())
            {
                Console.WriteLine($"  | {line.Text}");
            }

            session.Process.Log.LineAdded += (_, line) => Console.WriteLine($"  | {line.Text}");
            session.Process.Exited += (_, exit) =>
            {
                if (!exit.Expected)
                {
                    Console.WriteLine($"!! Xray завершился неожиданно, код {exit.ExitCode}.");

                    // Без ядра прокси ведёт в никуда — возвращаем настройки сразу (CLAUDE.md, правило 9).
                    RestoreSystemProxy("падение Xray");
                    stop.Cancel();
                }
            };

            if (systemProxy is not null)
            {
                systemProxy.Enable(SystemProxySettings.ForLocalHttp(session.HttpPort));
                Console.WriteLine($"Системный прокси Windows → 127.0.0.1:{session.HttpPort} (Edge, Chrome и др.).");
            }

            var works = await CheckThroughProxyAsync(session.HttpPort, stop.Token);

            Console.WriteLine();
            Console.WriteLine(works
                ? "Подключение работает. Можно проверить браузер или:"
                : "Xray запущен, но запрос через сервер не прошёл: сервер недоступен или не принял клиента (см. лог). Повторить вручную:");
            Console.WriteLine($"  curl.exe -x socks5h://127.0.0.1:{session.SocksPort} https://www.cloudflare.com/cdn-cgi/trace");
            Console.WriteLine("Ctrl+C — остановить Xray и выйти.");

            try
            {
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            catch (OperationCanceledException)
            {
            }

            var crashed = session.Process.HasExited && !(await session.Process.Completion).Expected;
            await session.StopAsync();
            Console.WriteLine(crashed ? "Остановлено после падения Xray." : "Xray остановлен.");
            return crashed ? 1 : 0;
        }
        finally
        {
            // При любом выходе, в том числе по исключению, прокси возвращается.
            RestoreSystemProxy("выход");
        }
    }
}

// Один запрос на проверку задержки через HTTP-вход Xray (как тест задержки, ROADMAP 1.8).
static async Task<bool> CheckThroughProxyAsync(int httpPort, CancellationToken ct)
{
    using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"), UseProxy = true };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    var stopwatch = Stopwatch.StartNew();
    try
    {
        using var response = await client.GetAsync(new Uri("https://cp.cloudflare.com/generate_204"), ct);
        Console.WriteLine($"Проверка через прокси: HTTP {(int)response.StatusCode} за {stopwatch.ElapsedMilliseconds} мс.");
        return response.StatusCode == HttpStatusCode.NoContent;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        Console.WriteLine($"Проверка через прокси не прошла за {stopwatch.ElapsedMilliseconds} мс: {ex.GetType().Name}. Смотрите лог Xray выше (--loglevel info — подробнее).");
        return false;
    }
}
