# Общие функции для deploy.ps1 и run-c3d.ps1: поиск установленных
# AutoCAD / Civil 3D, сборка и установка бандла.
#
# Файл только определяет функции — его подключают через точку:
#     . (Join-Path $PSScriptRoot 'acad-env.ps1')
#
# Ничего не захардкожено: версии, папки установки, профили и язык берутся
# из реестра, поэтому скрипты работают на любой машине с любым набором
# установленных релизов.

# AutoCAD 2025 перешёл на .NET 8; 2019-2024 — .NET Framework 4.7;
# 2015-2018 — .NET Framework 4.5. Сборка под старое API грузится и в новых
# релизах того же семейства, поэтому семейств три.
function Get-AcadTargetFramework {
    param([Parameter(Mandatory)][int]$Year)
    if ($Year -ge 2025) { return 'net8.0-windows' }
    if ($Year -ge 2019) { return 'net47' }
    return 'net45'
}

# Все установленные AutoCAD и его вертикали (Civil 3D, Map, Architecture...).
# Реестр: HKLM\SOFTWARE\Autodesk\AutoCAD\<серия>\<код продукта:язык>.
function Get-AcadInstall {
    [CmdletBinding()]
    param(
        # Год (2019) или серия (R23.0). Пустая строка — все установки.
        [string]$Release,

        # 'C3D' — только Civil 3D, 'ACAD' — только чистый AutoCAD.
        [ValidateSet('', 'C3D', 'ACAD')]
        [string]$Product = ''
    )

    $found = @()
    $seriesKeys = @(Get-ChildItem 'HKLM:\SOFTWARE\Autodesk\AutoCAD' -ErrorAction SilentlyContinue)

    foreach ($seriesKey in $seriesKeys) {
        $series = $seriesKey.PSChildName
        if ($series -notmatch '^R\d+\.\d+$') { continue }   # отсекаем InstalledProducts и мусор

        foreach ($productKey in @(Get-ChildItem $seriesKey.PSPath -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty $productKey.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { continue }

            # Ключ без Location — это не установка, а заготовка под язык.
            $location = $props.PSObject.Properties['Location']
            if (-not $location -or -not $location.Value) { continue }
            $dir = $location.Value
            $exe = Join-Path $dir 'acad.exe'
            if (-not (Test-Path $exe)) { continue }

            # Год: UPIRELEASE есть почти всегда, иначе вытаскиваем из имени папки.
            $year = 0
            $upi = $props.PSObject.Properties['UPIRELEASE']
            if ($upi -and $upi.Value -match '^\d{4}$') { $year = [int]$upi.Value }
            if ($year -eq 0 -and $dir -match '(20\d\d)') { $year = [int]$Matches[1] }
            if ($year -eq 0) { continue }

            $short = ''
            if ($props.PSObject.Properties['ProductNameShort']) { $short = [string]$props.ProductNameShort }
            $name = $short
            if ($props.PSObject.Properties['ProductName'] -and $props.ProductName) { $name = [string]$props.ProductName }

            # Civil 3D — тот же acad.exe, опознаётся по AecBase.dbx и короткому имени.
            $aecBase = Join-Path $dir 'AecBase.dbx'
            $isCivil = (Test-Path $aecBase) -and ($short -like 'C3D*')

            # Язык интерфейса. LocaleID лежит в реестре шестнадцатеричным:
            # 419 -> 0x419 -> 1049 -> ru-RU.
            $language = ''
            if ($props.PSObject.Properties['LocaleID'] -and $props.LocaleID) {
                try { $language = [System.Globalization.CultureInfo]::GetCultureInfo([Convert]::ToInt32([string]$props.LocaleID, 16)).Name }
                catch { $language = '' }
            }

            $found += [pscustomobject]@{
                Series     = $series
                Year       = $year
                Tfm        = Get-AcadTargetFramework -Year $year
                Product    = if ($isCivil) { 'C3D' } else { 'ACAD' }
                Name        = $name
                Location   = $dir
                Exe        = $exe
                AecBase    = if ($isCivil) { $aecBase } else { '' }
                Language   = $language
                ProductKey = $productKey.PSChildName
                HkcuPath   = "HKCU:\SOFTWARE\Autodesk\AutoCAD\$series\$($productKey.PSChildName)"
            }
        }
    }

    if ($Release) {
        $wanted = $Release.Trim()
        if ($wanted -match '^\d{4}$') { $found = @($found | Where-Object { $_.Year -eq [int]$wanted }) }
        else { $found = @($found | Where-Object { $_.Series -eq $wanted }) }
    }
    if ($Product) {
        $found = @($found | Where-Object { $_.Product -eq $Product })
    }

    # Новые релизы первыми: при автовыборе берём самый свежий.
    @($found | Sort-Object -Property Year -Descending)
}

# Профили, реально созданные в этом релизе (<<C3D_Metric>>, AutoCAD, ...).
function Get-AcadProfileName {
    param([Parameter(Mandatory)]$Install)
    $key = Join-Path $Install.HkcuPath 'Profiles'
    if (-not (Test-Path $key)) { return @() }
    @((Get-ChildItem $key -ErrorAction SilentlyContinue).PSChildName)
}

# '<<C3D_Metric>>', 'C3D_Metric' и 'metric' должны означать одно и то же:
# имена профилей различаются от версии к версии и от языка к языку.
function Resolve-AcadProfileName {
    param(
        [Parameter(Mandatory)]$Install,
        [string]$Name
    )
    $all = Get-AcadProfileName -Install $Install
    if (-not $Name) { return '' }
    if ($all.Count -eq 0) { return $Name }   # профилей не видно — передаём как есть

    $exact = @($all | Where-Object { $_ -eq $Name })
    if ($exact.Count -gt 0) { return $exact[0] }

    $bare = $Name.Trim('<', '>')
    $match = @($all | Where-Object { $_.Trim('<', '>') -eq $bare })
    if ($match.Count -gt 0) { return $match[0] }

    $match = @($all | Where-Object { $_ -like "*$bare*" })
    if ($match.Count -gt 0) { return $match[0] }

    throw ("Профиль <<{0}>> в {1} не найден. Доступны: {2}" -f $bare, $Install.Name, ($all -join ', '))
}

function Get-ZoomZoomBundlePath {
    Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\ZoomZoom.bundle'
}

# Плагин раньше назывался AcadRescale. Старый бандл обязан исчезнуть: иначе
# AutoCAD загрузит обе сборки сразу и одни и те же команды определятся дважды.
# DwGangsta теперь часть Зум-зума — её отдельный бандл тоже убираем: его команды
# (DWGSPLIT, DWGMERGE, DWGMOVEVP, DWGVIEWS) определились бы дважды.
function Remove-LegacyBundle {
    foreach ($name in 'AcadRescale.bundle', 'DwGangsta.bundle') {
        $legacy = Join-Path $env:APPDATA ('Autodesk\ApplicationPlugins\' + $name)
        if (Test-Path $legacy) {
            Remove-Item $legacy -Recurse -Force
            Write-Host "Удалён отдельный бандл $name — теперь он часть Зум-зума" -ForegroundColor Yellow
        }
    }
}

# Запущенный acad.exe держит ZoomZoom.dll и не даёт её перезаписать.
function Assert-AcadNotRunning {
    param([switch]$Quiet)
    $running = @(Get-Process acad -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return $true }
    if (-not $Quiet) {
        Write-Host ''
        Write-Host 'AutoCAD/Civil 3D уже запущен — DLL заблокирована, пересборка невозможна.' -ForegroundColor Yellow
        $running | Select-Object Id, @{n = 'Окно'; e = { $_.MainWindowTitle } } |
            Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Закройте его (сохранив работу) и запустите задачу снова.' -ForegroundColor Yellow
    }
    return $false
}

# Сборка. Без -Tfm собираются все платформы, под которые на машине есть API.
function Invoke-ZoomZoomBuild {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string]$Tfm
    )
    $proj = Join-Path $Root 'AcadRescale.csproj'
    $buildArgs = @($proj, '-c', 'Release', '-v', 'minimal')
    if ($Tfm) { $buildArgs += @('-f', $Tfm) }

    Write-Host '--- Сборка ---' -ForegroundColor Cyan
    & dotnet build @buildArgs
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }
}

# Раскладка бандла: Contents\<tfm>\ZoomZoom.dll, по одной DLL на семейство.
# Блоки <Components> для не собранных платформ из PackageContents.xml
# вырезаются — иначе AutoCAD ругается на отсутствующий модуль.
function Install-ZoomZoomBundle {
    param([Parameter(Mandatory)][string]$Root)

    $dest = Get-ZoomZoomBundlePath
    $contents = Join-Path $dest 'Contents'
    $built = @(Get-ChildItem (Join-Path $Root 'bin\Release') -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'ZoomZoom.dll') })

    if ($built.Count -eq 0) { throw 'В bin\Release нет ни одной собранной ZoomZoom.dll.' }

    Remove-LegacyBundle

    # Contents пересоздаём: иначе DLL от удалённого релиза останется лежать.
    if (Test-Path $contents) { Remove-Item $contents -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $contents | Out-Null

    foreach ($dir in $built) {
        $target = Join-Path $contents $dir.Name
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item (Join-Path $dir.FullName 'ZoomZoom.dll') $target -Force
        Write-Host ("  Contents\{0}\ZoomZoom.dll" -f $dir.Name) -ForegroundColor DarkGray
    }

    [xml]$xml = Get-Content (Join-Path $Root 'bundle\PackageContents.xml') -Raw
    $pkg = $xml.DocumentElement
    foreach ($comp in @($pkg.SelectNodes('Components'))) {
        $keep = $false
        foreach ($entry in @($comp.SelectNodes('ComponentEntry'))) {
            $rel = ($entry.GetAttribute('ModuleName') -replace '^\./', '') -replace '/', '\'
            if (Test-Path (Join-Path $dest $rel)) { $keep = $true }
        }
        if (-not $keep) {
            # Заодно убираем комментарий, который этот блок описывал.
            $note = $comp.PreviousSibling
            if ($note -and $note.NodeType -eq 'Comment') { [void]$pkg.RemoveChild($note) }
            [void]$pkg.RemoveChild($comp)
        }
    }
    $xml.Save((Join-Path $dest 'PackageContents.xml'))

    Write-Host "Плагин установлен: $dest" -ForegroundColor Green
    return $dest
}
