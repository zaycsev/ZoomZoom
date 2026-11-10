# Пересборка плагина, установка в ApplicationPlugins и запуск AutoCAD/Civil 3D.
# Вызывается из задач VS Code (Ctrl+Shift+B).
#
# Civil 3D — это тот же acad.exe, что и AutoCAD, только запущенный с ключами
# /ld AecBase.dbx и /product C3D.
#
# Ни версия, ни путь установки, ни имя профиля не захардкожены: всё берётся
# из реестра (см. acad-env.ps1). Без аргументов запускается самая новая
# установленная версия, Civil 3D предпочтительнее чистого AutoCAD.

[CmdletBinding()]
param(
    # Год (2019, 2024, 2026...) или серия (R23.0). Пусто — самая новая.
    [string]$Release,

    # 'C3D' — Civil 3D, 'ACAD' — чистый AutoCAD. Пусто — что найдётся.
    [ValidateSet('', 'C3D', 'ACAD')]
    [string]$Product = '',

    # Имя профиля: 'Metric', 'C3D_Russia', '<<C3D_Metric>>' — всё равно.
    # Сверяется с профилями, реально созданными в этом релизе.
    [Alias('Profile')]
    [string]$ProfileName,

    # Необязательно: чертёж, который открыть сразу после старта.
    [string]$Drawing,

    # Язык интерфейса (ru-RU). Пусто — как настроено в самой программе.
    [string]$Language,

    # Запустить уже установленный плагин, ничего не пересобирая.
    [switch]$NoBuild,

    # Показать, что будет сделано, ничего не собирая и не запуская.
    [switch]$DryRun,

    # Только перечислить найденные установки и профили.
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'acad-env.ps1')

# Задачи VS Code подставляют 'auto', когда выбор оставлен скрипту. Значение,
# начинающееся с дефиса, — это съеденный ключ (пустой аргумент пропал по пути),
# его тоже считаем «решай сам».
foreach ($name in 'Release', 'ProfileName', 'Product', 'Drawing', 'Language') {
    $value = (Get-Variable $name -ValueOnly)
    if ($value -eq 'auto' -or ($value -and $value.StartsWith('-'))) {
        Set-Variable $name -Value ''
    }
}

# --- какие версии вообще есть -----------------------------------------------

$all = @(Get-AcadInstall)
if ($all.Count -eq 0) {
    throw 'На этой машине не найдено ни одной установки AutoCAD или Civil 3D (реестр HKLM\SOFTWARE\Autodesk\AutoCAD пуст).'
}

if ($List) {
    Write-Host ''
    foreach ($i in $all) {
        Write-Host ("{0}  {1}" -f $i.Series, $i.Name) -ForegroundColor Cyan
        Write-Host ("  год {0}, продукт {1}, сборка {2}, язык {3}" -f $i.Year, $i.Product, $i.Tfm, $i.Language) -ForegroundColor DarkGray
        Write-Host ("  {0}" -f $i.Exe) -ForegroundColor DarkGray
        $profiles = @(Get-AcadProfileName -Install $i)
        if ($profiles.Count -gt 0) {
            Write-Host ("  профили: {0}" -f ($profiles -join ', ')) -ForegroundColor DarkGray
        }
        Write-Host ''
    }
    Write-Host ("Запуск: run-c3d.ps1 -Release {0}" -f $all[0].Year) -ForegroundColor Green
    exit 0
}

# --- выбор версии -----------------------------------------------------------

$candidates = @(Get-AcadInstall -Release $Release -Product $Product)
if ($candidates.Count -eq 0) {
    $have = ($all | ForEach-Object { "$($_.Year) $($_.Product)" }) -join ', '
    $asked = if ($Release) { $Release } else { 'любая' }
    throw "Не найдено: релиз '$asked', продукт '$Product'. Установлены: $have. Полный список — ключ -List."
}

# Civil 3D вперёд: проект про геодезию, чистый AutoCAD — запасной вариант.
$install = @($candidates | Sort-Object -Property @{e = { $_.Product -eq 'C3D' }; Descending = $true },
                                              @{e = { $_.Year }; Descending = $true })[0]

$resolvedProfile = Resolve-AcadProfileName -Install $install -Name $ProfileName

Write-Host ("Версия: {0}  ({1}, {2})" -f $install.Name, $install.Series, $install.Tfm) -ForegroundColor Cyan
if ($resolvedProfile) { Write-Host ("Профиль: {0}" -f $resolvedProfile) -ForegroundColor Cyan }

# --- сборка и установка -----------------------------------------------------

if (-not $DryRun -and -not $NoBuild) {
    if (-not (Assert-AcadNotRunning)) { exit 1 }
    Invoke-ZoomZoomBuild -Root $root -Tfm $install.Tfm
    $bundle = Install-ZoomZoomBundle -Root $root
}
else {
    $bundle = Get-ZoomZoomBundlePath
}

# --- запуск -----------------------------------------------------------------

$acadArgs = @()
if ($install.Product -eq 'C3D') {
    $acadArgs += @('/ld', "`"$($install.AecBase)`"", '/product', 'C3D')
}
if ($resolvedProfile) {
    $acadArgs += @('/p', "`"$resolvedProfile`"")
}
if ($Language) {
    $acadArgs += @('/language', $Language)
}
if ($Drawing) {
    if (-not (Test-Path $Drawing)) { throw "Чертёж не найден: $Drawing" }
    $acadArgs += "`"$(Resolve-Path $Drawing)`""
}

Write-Host ("--- Запуск {0} ---" -f $install.Name) -ForegroundColor Cyan
Write-Host ("{0} {1}" -f $install.Exe, ($acadArgs -join ' ')) -ForegroundColor DarkGray

if ($DryRun) {
    Write-Host 'DryRun: ничего не собрано и не запущено.' -ForegroundColor Yellow
    exit 0
}

if ($acadArgs.Count -gt 0) {
    Start-Process -FilePath $install.Exe -ArgumentList $acadArgs -WorkingDirectory $install.Location
}
else {
    Start-Process -FilePath $install.Exe -WorkingDirectory $install.Location
}

Write-Host ''
Write-Host 'Команды для проверки: ZOOMZOOM (ZZ), ZOOMZOOM1000' -ForegroundColor Green
Write-Host 'Если команда не найдена — плагин не автозагрузился; выполните NETLOAD и укажите:' -ForegroundColor DarkGray
Write-Host ("  {0}" -f (Join-Path $bundle "Contents\$($install.Tfm)\ZoomZoom.dll")) -ForegroundColor DarkGray
