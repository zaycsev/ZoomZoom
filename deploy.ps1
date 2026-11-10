# Пересборка и установка плагина под все установленные версии AutoCAD.
# Запускать при закрытом AutoCAD: он держит загруженную DLL и не даёт её
# перезаписать.

[CmdletBinding()]
param(
    # Собрать только одну платформу: net45 (2015-2018), net47 (2019-2024) или net8.0-windows (2025+).
    # По умолчанию — все, под которые на машине есть API.
    [string]$Tfm,

    # Не проверять запущенный AutoCAD (например, когда ставим впервые).
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'acad-env.ps1')

if (-not $Force -and -not (Assert-AcadNotRunning)) { exit 1 }

$installs = @(Get-AcadInstall)
if ($installs.Count -gt 0) {
    Write-Host 'Найденные версии:' -ForegroundColor Cyan
    $installs | ForEach-Object {
        Write-Host ("  {0,-6} {1,-45} {2}" -f $_.Series, $_.Name, $_.Tfm) -ForegroundColor DarkGray
    }
}

Invoke-ZoomZoomBuild -Root $root -Tfm $Tfm
Install-ZoomZoomBundle -Root $root | Out-Null

Write-Host ''
Write-Host 'Плагин загрузится сам при следующем старте AutoCAD/Civil 3D.' -ForegroundColor Green
Write-Host 'Команды: ZOOMZOOM (ZZ), ZOOMZOOMALL, ZOOMZOOMSEL, ZOOMZOOM1000' -ForegroundColor DarkGray
