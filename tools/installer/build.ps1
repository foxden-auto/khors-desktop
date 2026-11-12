<#
.SYNOPSIS
  Сборка установщика и переносимого ZIP KHORS Desktop (ROADMAP 5.1).

.DESCRIPTION
  1. Публикует khors-desktop.exe и khors-service.exe (Release, win-x64, один файл, со средой выполнения).
  2. Собирает каталог artifacts\installer\staging: оба exe, ядра из cores\win-x64 (с их LICENSE), LICENSE, NOTICE.
  3. ZIP: тот же каталог + файл-признак khors.portable (данные — в data\ рядом с exe).
  4. Установщик: ISCC.exe installer\khors.iss с версией из Directory.Build.props.

  Ядра должны быть скачаны: dotnet run tools/cores/fetch-cores.cs
  Inno Setup 6: путь к ISCC.exe — параметр -Iscc, иначе переменная INNO_SETUP_ISCC, иначе стандартные каталоги.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\installer\build.ps1
#>
[CmdletBinding()]
param(
    [string]$Iscc,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $root 'artifacts\installer'
$staging = Join-Path $out 'staging'

$version = (& dotnet msbuild (Join-Path $root 'src\Khors.App\Khors.App.csproj') -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or -not $version) { throw 'Не удалось прочитать Version из Directory.Build.props.' }
Write-Host "KHORS Desktop $version"

$cores = Join-Path $root 'cores\win-x64'
foreach ($core in 'xray\xray.exe', 'sing-box\sing-box.exe', 'sing-box-extended\sing-box.exe') {
    if (-not (Test-Path (Join-Path $cores $core))) { throw "Нет ядра $core — выполните: dotnet run tools/cores/fetch-cores.cs" }
}

# Каталог сборки — наш собственный, пересобирается целиком.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null

$publish = @('-c', 'Release', '-r', 'win-x64', '--self-contained', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-o', $staging)
foreach ($project in 'src\Khors.App', 'src\Khors.Service') {
    & dotnet publish (Join-Path $root $project) @publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project завершился с кодом $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force (Join-Path $staging 'cores') | Out-Null
Copy-Item -Recurse (Join-Path $cores 'xray') (Join-Path $staging 'cores')
Copy-Item -Recurse (Join-Path $cores 'sing-box') (Join-Path $staging 'cores')
Copy-Item -Recurse (Join-Path $cores 'sing-box-extended') (Join-Path $staging 'cores')
Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'NOTICE') $staging

# Только то, что поставляется: публикация могла оставить лишнее (например, .pdb при другой настройке).
$expected = @('khors-desktop.exe', 'khors-service.exe', 'cores', 'LICENSE', 'NOTICE')
Get-ChildItem $staging | Where-Object { $expected -notcontains $_.Name } | ForEach-Object {
    throw "Неожиданный файл в сборке: $($_.Name)"
}

# Переносимый ZIP.
$portable = Join-Path $out "khors-desktop-$version-win-x64-portable.zip"
$portableDir = Join-Path $out 'portable'
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
Copy-Item -Recurse $staging $portableDir
Set-Content -Path (Join-Path $portableDir 'khors.portable') -Encoding utf8 -Value @(
    'KHORS Desktop portable: profiles and settings are stored in the data folder next to khors-desktop.exe.',
    'Переносимая версия KHORS: профили и настройки хранятся в папке data рядом с khors-desktop.exe.')
if (Test-Path $portable) { Remove-Item $portable -Force }
# Записи — с «/» вручную: Compress-Archive и ZipFile в Windows PowerShell 5.1 пишут пути с «\», и не все распаковщики
# создают из них папки.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($portable, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem $portableDir -Recurse -File | ForEach-Object {
        $name = $_.FullName.Substring($portableDir.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $zip.Dispose()
}
Remove-Item $portableDir -Recurse -Force
Write-Host "ZIP: $portable"

if ($SkipInstaller) { return }

if (-not $Iscc) { $Iscc = $env:INNO_SETUP_ISCC }
if (-not $Iscc) {
    $Iscc = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw 'Не найден ISCC.exe (Inno Setup 6). Укажите -Iscc или INNO_SETUP_ISCC, либо соберите только ZIP: -SkipInstaller.' }

& $Iscc "/DAppVersion=$version" "/DStaging=$staging" "/DOutputDir=$out" (Join-Path $root 'installer\khors.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC завершился с кодом $LASTEXITCODE" }
Write-Host "Установщик: $(Join-Path $out "khors-desktop-$version-win-x64-setup.exe")"
