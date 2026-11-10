# Сборка установщика «Зум-зум»: все три сборки плагина + Inno Setup.
#
#   .\build-installer.ps1 [-Version "1.1.1"] [-InnoSetup <путь к ISCC.exe>]
#
# Результат: dist\Setup_ZoomZoom_v<версия>.exe. Нужны .NET SDK 8+, Inno Setup 6
# и установленный AutoCAD / Civil 3D 2019–2024 (API для сборки net47; net45 и
# net8.0-windows берут API из пакетов Autodesk в NuGet). Версия из параметра
# подставляется в DLL, PackageContents.xml и установщик.
# Картинки мастера лежат в installer\ (готовые PNG и setup.ico).
[CmdletBinding()]
param(
    [string]$Version = '1.1.1',
    [string]$InnoSetup
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step([string]$text) { Write-Host "`n> $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  OK: $text" -ForegroundColor Green }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Версия должна быть вида 1.2.3: $Version" }

if (-not $InnoSetup) {
    $InnoSetup = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $InnoSetup -or -not (Test-Path $InnoSetup)) {
    throw 'Inno Setup 6 не найден (https://jrsoftware.org/isdl.php); укажите -InnoSetup <путь к ISCC.exe>.'
}

# --- 1. Сборка ---------------------------------------------------------------
Step "Сборка ZoomZoom.dll $Version"
$tfms = 'net45', 'net47', 'net8.0-windows'
foreach ($tfm in $tfms) {
    $dll = Join-Path $root "bin\Release\$tfm\ZoomZoom.dll"
    if (Test-Path $dll) { Remove-Item $dll -Force }
}
& dotnet build (Join-Path $root 'AcadRescale.csproj') -c Release -v minimal --nologo "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }

# --- 2. Бандл ----------------------------------------------------------------
Step 'Сборка dist\bundle'
$bundle = Join-Path $root 'dist\bundle'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
foreach ($tfm in $tfms) {
    $dll = Join-Path $root "bin\Release\$tfm\ZoomZoom.dll"
    # Установщик обязан нести все три сборки: без одной из них пользователи
    # целого поколения AutoCAD останутся без плагина.
    if (-not (Test-Path $dll)) { throw "Не собралась $tfm\ZoomZoom.dll (нет API этого поколения AutoCAD?)" }
    $target = Join-Path $bundle "Contents\$tfm"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $dll $target
    Ok ("Contents\{0}\ZoomZoom.dll ({1} КБ)" -f $tfm, [math]::Round((Get-Item $dll).Length / 1KB))
}

[xml]$xml = Get-Content (Join-Path $root 'bundle\PackageContents.xml') -Raw -Encoding UTF8
$xml.DocumentElement.SetAttribute('AppVersion', $Version)
foreach ($entry in @($xml.SelectNodes('//ComponentEntry'))) { $entry.SetAttribute('Version', $Version) }
$xml.Save((Join-Path $bundle 'PackageContents.xml'))
Ok 'PackageContents.xml'

# --- 3. Inno Setup -----------------------------------------------------------
Step 'Inno Setup'
& $InnoSetup /Q "/DAppVersion=$Version" (Join-Path $root 'installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup завершился с ошибкой.' }

$exe = Join-Path $root "dist\Setup_ZoomZoom_v$Version.exe"
if (-not (Test-Path $exe)) { throw "Установщик не создан: $exe" }
Write-Host ''
Write-Host ("  Установщик готов: {0} ({1} МБ)" -f $exe, [math]::Round((Get-Item $exe).Length / 1MB, 1)) -ForegroundColor Green
