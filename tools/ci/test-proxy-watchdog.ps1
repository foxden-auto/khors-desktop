# Сквозная проверка сторожа системного прокси (только CI: меняет системный прокси).
# Запускает khors-devcli с --system-proxy, жёстко завершает его (TerminateProcess) и проверяет,
# что сторож вернул прежние настройки прокси за ≤ 5 с.
param([string]$DevCli = "artifacts/devcli-win-x64/khors-devcli.exe")

$ErrorActionPreference = "Stop"
$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
$output = Join-Path ([IO.Path]::GetTempPath()) "khors-devcli-watchdog.txt"

function Get-ProxyState {
    $p = Get-ItemProperty $key
    [pscustomobject]@{ Enable = [int]$p.ProxyEnable; Server = [string]$p.ProxyServer }
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return (& $Condition)
}

$before = Get-ProxyState
# Вымышленный профиль на недоступный локальный сервер: Xray запускается без выхода в сеть.
$link = "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@127.0.0.1:1#watchdog-test"
$process = Start-Process $DevCli -ArgumentList $link, "--system-proxy", "--socks", "0", "--http", "0", "--no-wait" `
    -PassThru -WindowStyle Hidden -RedirectStandardOutput $output

try {
    $enabled = Wait-Until { $s = Get-ProxyState; $s.Enable -eq 1 -and $s.Server -like "127.0.0.1:*" } 30
    if (-not $enabled) { throw "Системный прокси не включился: $(Get-ProxyState | ConvertTo-Json -Compress)" }

    Stop-Process -Id $process.Id -Force

    $restored = Wait-Until { $s = Get-ProxyState; $s.Enable -eq $before.Enable -and $s.Server -eq $before.Server } 5
    if (-not $restored) {
        throw "Сторож не вернул прокси: было $($before | ConvertTo-Json -Compress), стало $(Get-ProxyState | ConvertTo-Json -Compress)"
    }

    Write-Host "OK: после жёсткого завершения сторож вернул системный прокси."
}
finally {
    Get-Process xray, khors-devcli -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $output) { Get-Content $output -Encoding utf8 }
}
