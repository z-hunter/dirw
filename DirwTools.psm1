# Store the module root at load time for reliable access in functions
$SizeW_ModuleRoot = Split-Path $MyInvocation.MyCommand.Path -Parent

function Format-FileSize {
    param ([long]$bytes)
    if ($bytes -ge 1GB) { "{0:N1} GB" -f ($bytes / 1GB) }
    elseif ($bytes -ge 1MB) { "{0:N1} MB" -f ($bytes / 1MB) }
    elseif ($bytes -ge 1KB) { "{0:N1} KB" -f ($bytes / 1KB) }
    else { "$bytes B" }
}

function Import-SizeWAssembly {
    if ("SizeW.Program" -as [type]) { return $true }
    
    $isCore = $PSVersionTable.PSVersion.Major -ge 6
    $tfm = if ($isCore) { "net8.0" } else { "net48" }
    
    $originPath = Join-Path $SizeW_ModuleRoot "sizew\publish\$tfm"
    $originDll = Join-Path $originPath "sizew.dll"
    if (-not (Test-Path $originDll)) {
        $originDll = Join-Path $originPath "sizew.exe"
    }

    # Fallback to older logic if specific TFM folder absent (backward compatibility during dev)
    if (-not (Test-Path $originDll)) {
        $originPath = Join-Path $SizeW_ModuleRoot "sizew\publish"
        $originDll = Join-Path $originPath "sizew.dll"
    }

    if (-not (Test-Path $originDll)) {
        return $false 
    }

    # Shadow Copy: Copy entire publish folder to a unique temp directory
    # This avoids locks and ensures all dependencies (.json etc) are available
    $tempDir = Join-Path $env:TEMP "sizew_cache_$([Guid]::NewGuid().Guid.Substring(0,8))"
    try {
        New-Item -ItemType Directory -Path $tempDir -Force -ErrorAction SilentlyContinue | Out-Null
        Copy-Item (Join-Path $originPath "*") $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        
        $fileName = Split-Path $originDll -Leaf
        $tempDll = Join-Path $tempDir $fileName
        Add-Type -Path $tempDll -ErrorAction Stop
    }
    catch {
        # Add-Type might fail on .exe in PS 5.1, so we fallback to LoadFrom
        if (-not ("SizeW.Program" -as [type])) {
            try { [System.Reflection.Assembly]::LoadFrom($tempDll) > $null } catch { 
                Write-Debug "LoadFrom also failed: $($_.Exception.Message)"
                return $false 
            }
        }
    }
    return $null -ne ("SizeW.Program" -as [type])
}

function Get-FolderSize {
    param ($folderPath, $useCache = $false)
    
    if ($useCache) {
        if (Import-SizeWAssembly) {
            try {
                $SizeW = "SizeW.Program" -as [type]
                return $SizeW::LibMeasureDirectory($folderPath, $true, $false, $false)
            }
            catch {
                Write-Debug "DLL call failed: $($_.Exception.Message)"
            }
        }
        else {
            Write-Warning "sizew.dll not found in 'sizew\publish\'. Use Install-SizeWBinaries to compile it. Falling back to slow scan."
        }
    }

    try {
        (Get-ChildItem -LiteralPath $folderPath -Recurse -Force -File -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum
    }
    catch {
        0
    }
}

function Install-SizeWBinaries {
    <#
.SYNOPSIS
  Compiles the sizew utility from source.
.DESCRIPTION
  Requires dotnet SDK to be installed. Compiles sizew into the module directory for use with dirw -c.
#>
    $modulePath = $PSScriptRoot
    $sourcePath = Join-Path $modulePath "sizew"
    
    if (-not (Test-Path $sourcePath)) {
        Write-Error "Source for sizew not found at $sourcePath"
        return
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error "dotnet SDK not found. Please install it from https://dotnet.microsoft.com/download"
        return
    }

    Write-Host "Compiling sizew utility (net8.0 + net48)..." -ForegroundColor Cyan
    $publishPath = Join-Path $sourcePath "publish"
    
    try {
        # Publish for .NET 8.0 (Modern PS)
        $outNet8 = Join-Path $publishPath "net8.0"
        dotnet publish $sourcePath -c Release -f net8.0 -o $outNet8 --self-contained false -p:PublishReadyToRun=true
        
        # Publish for .NET Framework 4.8 (Windows PowerShell 5.1)
        $outNet48 = Join-Path $publishPath "net48"
        dotnet publish $sourcePath -c Release -f net48 -o $outNet48
        
        if ((Test-Path (Join-Path $outNet8 "sizew.exe")) -and (Test-Path (Join-Path $outNet48 "sizew.exe"))) {
            Write-Host "sizew binaries successfully compiled and installed in $publishPath" -ForegroundColor Green
        }
        else {
            Write-Error "Failed to verify compiled binaries."
        }
    }
    catch {
        Write-Error "Compilation failed: $($_.Exception.Message)"
    }
}

function sizew {
    <#
.SYNOPSIS
  Fast cached directory size calculator.
.DESCRIPTION
  Uses a local binary cache to avoid re-scanning unchanged directories.
.PARAMETER Path
  Path to measure.
.PARAMETER Recursive
  Calculate size of all subdirectories.
.PARAMETER BypassCache
  Do not read or write to the cache.
.PARAMETER Recalculate
  Force re-scan of the directory but update the cache.
.PARAMETER Raw
  Output raw size in bytes.
#>
    param(
        [Parameter(Position = 0)]
        [string]$Path = ".",

        [Alias('r')]
        [switch]$Recursive,

        [Alias('bc')]
        [switch]$BypassCache,

        [Alias('rc')]
        [switch]$Recalculate,

        [switch]$ShowDebug,
        [switch]$Raw,

        [Alias('h')]
        [switch]$Help
    )
    
    if (-not (Import-SizeWAssembly)) {
        Write-Error "SizeW binaries not found in 'sizew\publish\'. Please run Install-SizeWBinaries."
        return
    }

    if ($Help) {
        # Assuming the Program class has a main function that showing help on no args, 
        # but since we are calling LibMeasureDirectory directly, we might need a dedicated Help method or trigger the EXE help.
        # However, LibMeasureDirectory doesn't print help. The easiest way is to mimic the internal help or call Main with args.
        
        # Let's call Main with -h to use the existing help logic in C#
        $SizeW = "SizeW.Program" -as [type]
        $SizeW::Main(@("-h"))
        return
    }

    if ($ShowDebug) { 
        $SizeW = "SizeW.Program" -as [type]
        $SizeW::LibSetDebug($true) 
    }
    
    $SizeW = "SizeW.Program" -as [type]
    $size = $SizeW::LibMeasureDirectory(
        (Resolve-Path $Path).Path, 
        $Recursive, 
        $BypassCache, 
        $Recalculate
    )

    if ($Raw) {
        return $size
    }
    else {
        return Format-FileSize $size
    }
}

function dirw {
    <#
.SYNOPSIS
  Multi-column advanced DIR-like file listing for PowerShell.
.DESCRIPTION
  Shows files and folders in multi-column format, like DIR /W, with color and (optional) file info. Supports sorting.
.PARAMETER Target
  Folder or wildcard pattern (e.g. C:\Temp\*.txt). Default: current directory.
.PARAMETER l
  Show sizes/dates (long format).
.PARAMETER s
  Calculate folder sizes (recursive, slow on big dirs; implies -l).
.PARAMETER a
  Show hidden files/directories.
.PARAMETER sn
  Sort by name (default).
.PARAMETER ss
  Sort by size.
.PARAMETER sd
  Sort by date.
.PARAMETER sortName
  Sort by name (long key).
.PARAMETER sortSize
  Sort by size (long key).
.PARAMETER sortDate
  Sort by date (long key).
.PARAMETER oa
  Order ascending (default).
.PARAMETER od
  Order descending.
.PARAMETER orderAscending
  Order ascending (long key).
.PARAMETER orderDescending
  Order descending (long key).
.EXAMPLE
  dirw -l -a -ss -od
  dirw c:\temp\*.txt -ss
.NOTES
  Author: Michael.Voitovich@gmail.com
#>
    [CmdletBinding(PositionalBinding = $false)]
    param (
        [Parameter(Position = 0)]
        [string]$Target = ".",

        [switch]$l,
        [switch]$s,
        [switch]$a,
        [switch]$h,

        [switch]$long,
        [switch]$size,
        [switch]$all,
        [switch]$help,
        [switch]$c,
        [switch]$cachedSize,

        [switch]$sn,
        [switch]$ss,
        [switch]$sd,
        [switch]$sortName,
        [switch]$sortSize,
        [switch]$sortDate,

        [switch]$oa,
        [switch]$od,
        [switch]$orderAscending,
        [switch]$orderDescending
    )

    if ($h -or $help) {
        Write-Host "Usage: dirw [path or pattern] [-l|-long] [-s|-size] [-c|-cachedSize] [-a|-all] [-sn|-ss|-sd] [-oa|-od] [-h|-help]" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Options:"
        Write-Host "  path/pattern     Folder or wildcard pattern (default: . )"
        Write-Host "  -l, -long        Show sizes and dates (long format)"
        Write-Host "  -s, -size        Calculate size of folders (implies -l)"
        Write-Host "  -c, -cachedSize  Use sizew utility for cached folder sizes (implies -s)"
        Write-Host "  -a, -all         Show hidden files and folders"
        Write-Host "  -sn, -sortName   Sort by name (default)"
        Write-Host "  -ss, -sortSize   Sort by size"
        Write-Host "  -sd, -sortDate   Sort by date"
        Write-Host "  -oa, -orderAscending    Order ascending (default)"
        Write-Host "  -od, -orderDescending   Order descending"
        Write-Host "  -h, -help        Show this help"
        Write-Host ""
        Write-Host "Examples:"
        Write-Host "  dirw"
        Write-Host "      Show all files and folders in the current directory"
        Write-Host "  dirw *.ps1 -sd -od"
        Write-Host "      Show .ps1 files sorted by date, newest first"
        Write-Host "  dirw C:\temp -l -ss"
        Write-Host "      Show all files in C:\temp\ in long format, sorted by size (smallest to largest)"
        Write-Host "  dirw C:\windows\*.exe -long -sortSize -a"
        Write-Host "      Show all .exe files in C:\windows\ in long format, including hidden files"
        Write-Host "  dirw C:\windows\ -s -a -od"
        Write-Host "      Show all items in C:\windows\ (long format, folder sizes calculated, sorted largest to smallest, including hidden)"


        Write-Host ""
        return
    }


    $Long = $l -or $long
    $ShowHidden = $a -or $all
    $CalculateFolderSize = $s -or $size -or $c -or $cachedSize
    $UseCache = $c -or $cachedSize

    # Если -s или -size, обязательно включаем Long
    if ($CalculateFolderSize) { $Long = $true }

    # Определяем тип сортировки
    $SortBy = "Name"
    if ($ss -or $sortSize) { $SortBy = "Size" }
    elseif ($sd -or $sortDate) { $SortBy = "Date" }
    elseif ($sn -or $sortName) { $SortBy = "Name" }

    # Определяем направление сортировки
    $SortDescending = $false
    if ($od -or $orderDescending) { $SortDescending = $true }
    elseif ($oa -or $orderAscending) { $SortDescending = $false }

    if (-not $Target) { $Target = "." }
    $parentPath = (Split-Path $Target -Parent)
    if ($parentPath -and -not (Test-Path $parentPath)) {
        Write-Host "Path not found: $parentPath" -ForegroundColor Red
        return
    }

    $items = if ($ShowHidden) {
        Get-ChildItem -Path $Target -Force
    }
    else {
        Get-ChildItem -Path $Target | Where-Object { -not $_.Attributes.ToString().Contains("Hidden") }
    }

    if (-not $items) {
        Write-Host "(nothing found)"
        return
    }

    $execExtensions = @('.exe', '.bat', '.cmd', '.ps1', '.msi')

    # Сортировка
    if ($SortBy -eq "Size" -and $CalculateFolderSize) {
        foreach ($item in $items) {
            if ($item.PSIsContainer) {
                $item | Add-Member -NotePropertyName RealLength -NotePropertyValue (Get-FolderSize $item.FullName -useCache $UseCache)
            }
            else {
                $item | Add-Member -NotePropertyName RealLength -NotePropertyValue $item.Length
            }
        }
        $items = if ($SortDescending) {
            $items | Sort-Object -Property RealLength -Descending
        }
        else {
            $items | Sort-Object -Property RealLength
        }
    }
    elseif ($SortBy -eq "Size") {
        $items = if ($SortDescending) {
            $items | Sort-Object -Property Length -Descending
        }
        else {
            $items | Sort-Object -Property Length
        }
    }
    elseif ($SortBy -eq "Date") {
        $items = if ($SortDescending) {
            $items | Sort-Object -Property LastWriteTime -Descending
        }
        else {
            $items | Sort-Object -Property LastWriteTime
        }
    }
    else {
        $items = if ($SortDescending) {
            $items | Sort-Object -Property Name -Descending
        }
        else {
            $items | Sort-Object -Property Name
        }
    }

    if ($Long) {
        $maxNameLength = ($items | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
        $maxSizeLength = 10

        $rows = @()
        foreach ($item in $items) {
            $name = $item.Name.PadRight($maxNameLength)
            $color = "White"
            if ($item.PSIsContainer) {
                if ($CalculateFolderSize) {
                    if ($item.PSObject.Properties["RealLength"]) {
                        $folderBytes = $item.RealLength
                    }
                    else {
                        $folderBytes = Get-FolderSize $item.FullName -useCache $UseCache
                    }
                    $sizeVal = Format-FileSize($folderBytes)
                }
                else {
                    $sizeVal = "<DIR>"
                }
                $color = "Yellow"
            }
            else {
                $sizeVal = Format-FileSize($item.Length)
                if ($execExtensions -contains $item.Extension.ToLower()) {
                    $color = "Green"
                }
            }

            $sizeText = $sizeVal.PadLeft($maxSizeLength)
            $date = $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
            $rowText = "$name  $sizeText  $date"
            $rows += [PSCustomObject]@{ Text = $rowText; Color = $color }
        }

        $header = ("Name".PadRight($maxNameLength) + "  " + "Size".PadLeft($maxSizeLength) + "  " + "Modified")
        $maxRowLength = ($rows | ForEach-Object { $_.Text.Length } | Measure-Object -Maximum).Maximum
        $consoleWidth = [console]::WindowWidth
        $spacing = 6
        $columnWidth = $maxRowLength + $spacing
        $columns = [Math]::Max(1, [Math]::Floor($consoleWidth / $columnWidth))

        # Печатаем заголовок над каждой колонкой
        $headerLine = ""
        for ($colIdx = 0; $colIdx -lt $columns; $colIdx++) {
            $headerLine += $header.PadRight($columnWidth)
        }
        Write-Host $headerLine -ForegroundColor Cyan

        # Выводим в несколько колонок
        for ($i = 0; $i -lt $rows.Count; $i += $columns) {
            for ($j = 0; $j -lt $columns; $j++) {
                $index = $i + $j
                if ($index -lt $rows.Count) {
                    $row = $rows[$index]
                    Write-Host -NoNewline $row.Text.PadRight($columnWidth) -ForegroundColor $row.Color
                }
            }
            Write-Host ""
        }
    }
    else {
        # краткий вывод в колонках
        $maxNameLength = ($items | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum
        $consoleWidth = [console]::WindowWidth
        $spacing = 2
        $columnWidth = $maxNameLength + $spacing
        $columns = [Math]::Max(1, [Math]::Floor($consoleWidth / $columnWidth))

        $i = 0
        foreach ($item in $items) {
            $name = $item.Name
            $padded = $name.PadRight($columnWidth)

            if ($item.PSIsContainer) {
                $color = 'Yellow'
            }
            elseif ($execExtensions -contains $item.Extension.ToLower()) {
                $color = 'Green'
            }
            else {
                $color = 'White'
            }

            Write-Host -NoNewline $padded -ForegroundColor $color
            $i++
            if ($i -ge $columns) {
                $i = 0
                Write-Host ""
            }
        }

        if ($i -ne 0) { Write-Host "" }
    }
    Write-Host ""
}
Export-ModuleMember -Function dirw, sizew, Install-SizeWBinaries
