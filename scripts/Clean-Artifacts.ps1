#requires -Version 7.0
<#
.SYNOPSIS
Preview old Stargate artifacts; add -Delete to remove them.
.DESCRIPTION
Removes old test outputs, duplicate packages, and project bin/obj directories.
Keeps the current cooked cache, galaxy catalogue, current published runtimes,
source/tool folders, saves/databases/identities, and running or recently used
output groups. Does not touch D:, normal user data, assets, assets_itch or Git.
The default age is two hours, measured from both creation and modification time.
No processes are stopped. Reports are saved under cleanup-reports.
.EXAMPLE
pwsh -File ./scripts/Clean-Artifacts.ps1
.EXAMPLE
pwsh -File ./scripts/Clean-Artifacts.ps1 -Delete
.EXAMPLE
pwsh -File ./scripts/Clean-Artifacts.ps1 -Delete -MinAgeHours 0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Repo = (Split-Path $PSScriptRoot -Parent),
    [ValidateRange(0,87600)][double]$MinAgeHours = 2,
    [string[]]$Protect = @(),
    [switch]$Delete
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Repo = [IO.Path]::GetFullPath($Repo).TrimEnd('\','/')
if (!(Test-Path -LiteralPath "$Repo/src/StargateGame.Server/StargateGame.Server.csproj")) {
    throw 'Not a StargateGame checkout.'
}
$cutoff = [DateTime]::UtcNow.AddHours(-$MinAgeHours)
$art = Join-Path $Repo 'artifacts'
$protected = @($Protect | ForEach-Object {
    if ([IO.Path]::IsPathRooted($_)) { [IO.Path]::GetFullPath($_) }
    else { [IO.Path]::GetFullPath((Join-Path $Repo $_)) }
})
# These may contain source, imported data, or current runtime dependencies.
$keepArtifacts = @('galaxy','native-source','reference-tools','JoltPhysicsSharp',
    'asset-tools','building-kit','settlement-meshes','terrain-library-prototype')
$currentPackages = @('client-win-x64','server-win-x64','server-linux-x64')

function Under([string]$Path, [string]$Boundary) {
    $Path = [IO.Path]::GetFullPath($Path)
    $Boundary = [IO.Path]::GetFullPath($Boundary)
    $Path.Equals($Boundary,[StringComparison]::OrdinalIgnoreCase) -or
        $Path.StartsWith($Boundary.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}
function Safe-Path([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!(Under $full $Boundary) -or !(Under $Boundary $Repo) -or $Boundary -eq $Repo) {
        throw "Path outside cleanup boundary: $full"
    }
    for ($cursor=$full; $cursor; $cursor=Split-Path $cursor -Parent) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Link retained: $cursor"
        }
    }
    $full
}
function Process-List { @(Get-CimInstance Win32_Process -ErrorAction Stop) }
function Build-Active($Processes) {
    @($Processes | Where-Object {
        ($_.Name -match '^(dotnet|MSBuild)\.exe$' -and $_.CommandLine -and
         $_.CommandLine -notmatch '(?i)[-/]nodemode[:=]1\b' -and
         ($_.CommandLine -match '(?i)dotnet(?:\.exe)?"?\s+(build|publish|test|run|restore|msbuild)\b|\bMSBuild\.dll\b' -or $_.Name -eq 'MSBuild.exe')) -or
        ($_.Name -match '^python.*\.exe$' -and $_.CommandLine -match '(?i)(build_asset_packs|cook_terrain|import_|bake_)')
    }).Count -gt 0
}
function In-Use([string]$Root, $Processes) {
    $relative = [IO.Path]::GetRelativePath($Repo,$Root).Replace('/','\')
    foreach ($p in $Processes) {
        if ($p.ExecutablePath -and (Under $p.ExecutablePath $Root)) { return $true }
        $command = ([string]$p.CommandLine).Replace('/','\')
        if ($command.IndexOf($Root,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $command.IndexOf($relative,[StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        if ($p.Name -like 'StargateGame*' -and !$p.ExecutablePath -and !$p.CommandLine) { return $true }
    }
    $false
}
function Inventory([string]$Root) {
    $files = [Collections.Generic.List[object]]::new()
    $dirs = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Root)
    $newest = [DateTime]::MinValue; $reason = ''; $bytes = 0L
    while ($pending.Count) {
        $path = $pending.Pop(); $dirs.Add($path)
        $info = Get-Item -LiteralPath $path -Force
        foreach ($stamp in @($info.LastWriteTimeUtc,$info.CreationTimeUtc)) { if ($stamp -gt $newest) { $newest=$stamp } }
        $children = @(Get-ChildItem -LiteralPath $path -Force)
        # Retain entire database-containing directories, including adjacent identities.
        if (@($children | Where-Object { !$_.PSIsContainer -and $_.Name -match '(?i)\.(db|sqlite|sqlite3)([-.].*)?$|\.(dpapi|pem|key)$|^(identity|device-identity|address-book|settings)\.json$' }).Count) { continue }
        foreach ($item in $children) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $reason='Contains a junction/link'; continue }
            if ($item.PSIsContainer) {
                if ($item.Name -notmatch '(?i)^(saves?|worlds?|universes?|backups?|profiles?|\.git)$') { $pending.Push($item.FullName) }
                continue
            }
            foreach ($stamp in @($item.LastWriteTimeUtc,$item.CreationTimeUtc)) { if ($stamp -gt $newest) { $newest=$stamp } }
            $files.Add([pscustomobject]@{Path=$item.FullName;Bytes=$item.Length;Ticks=$item.LastWriteTimeUtc.Ticks;Created=$item.CreationTimeUtc.Ticks})
            $bytes += $item.Length
        }
    }
    [pscustomobject]@{Files=$files;Directories=$dirs;Newest=$newest;Bytes=$bytes;Reason=$reason}
}

$roots = [Collections.Generic.List[object]]::new()
function Add-Root([string]$Path,[string]$Kind) {
    if (Test-Path -LiteralPath $Path -PathType Container) { $roots.Add([pscustomobject]@{Path=[IO.Path]::GetFullPath($Path);Kind=$Kind}) }
}
if (Test-Path -LiteralPath $art) {
    foreach ($dir in Get-ChildItem -LiteralPath $art -Directory -Force) {
        if ($dir.Name -in $keepArtifacts) { continue }
        if ($dir.Name -eq 'cooked') {
            foreach ($leaf in @('bin','obj')) { Add-Root (Join-Path $dir.FullName $leaf) 'Cooker build output' }
        } elseif ($dir.Name -in @('validation','asset-packs','publish')) {
            foreach ($child in Get-ChildItem -LiteralPath $dir.FullName -Directory -Force) {
                if ($dir.Name -eq 'publish' -and $child.Name -in $currentPackages) { continue }
                Add-Root $child.FullName 'Historical output/package'
            }
        } else { Add-Root $dir.FullName 'Historical test artifacts' }
    }
}
# Only direct bin/obj siblings of known project files, never arbitrary source directories.
foreach ($area in @('src','tests','tools','benchmarks')) {
    $base = Join-Path $Repo $area
    if (!(Test-Path -LiteralPath $base)) { continue }
    foreach ($project in Get-ChildItem -LiteralPath $base -Directory) {
        if ($project.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if (@(Get-ChildItem -LiteralPath $project.FullName -Filter '*.csproj' -File).Count) {
            foreach ($leaf in @('bin','obj')) { Add-Root (Join-Path $project.FullName $leaf) 'Project build output' }
        }
    }
}
$running = Process-List
$rows = [Collections.Generic.List[object]]::new(); $inventories = @{}
foreach ($root in $roots) {
    $reason = ''
    try { $null=Safe-Path $root.Path $root.Path } catch { $reason=$_.Exception.Message }
    if ($reason) { $rows.Add([pscustomobject]@{Path=$root.Path;Kind=$root.Kind;Bytes=0L;Action='Keep';Reason=$reason}); continue }
    $scan=Inventory $root.Path; $reason=$scan.Reason
    if ($scan.Newest -gt $cutoff) { $reason='Used/created within age limit' }
    if (@($protected | Where-Object { (Under $root.Path $_) -or (Under $_ $root.Path) }).Count) { $reason='Explicitly protected' }
    if (In-Use $root.Path $running) { $reason='Running process uses this output' }
    if (!$scan.Files.Count) { $reason='No removable files (empty or protected data)' }
    $rows.Add([pscustomobject]@{Path=$root.Path;Kind=$root.Kind;Bytes=$scan.Bytes;Action=$(if($reason){'Keep'}else{'Delete'});Reason=$reason})
    $inventories[$root.Path]=$scan
}
$candidates=@($rows | Where-Object Action -eq 'Delete')
$bytes=0L; foreach ($row in $candidates) { $bytes+=$row.Bytes }
$report=Join-Path $Repo ('cleanup-reports/artifacts-'+(Get-Date -Format yyyyMMdd-HHmmss)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
$null=Safe-Path $report (Join-Path $Repo 'cleanup-reports')
New-Item -ItemType Directory -Path $report -Force -WhatIf:$false | Out-Null
$rows | Export-Csv -LiteralPath "$report/inventory.csv" -NoTypeInformation -WhatIf:$false
$rows | Sort-Object Bytes -Descending | Select-Object Action,@{n='GiB';e={[math]::Round($_.Bytes/1GB,2)}},Path,Reason | Format-Table -AutoSize
Write-Host ('Eligible: {0:N2} GiB in {1} output groups. Report: {2}' -f ($bytes/1GB),$candidates.Count,$report)
Write-Host 'Kept: cooked cache, galaxy, current publish runtimes, source/tool folders, saves/identities, running/recent outputs.'
if (!$Delete -or $WhatIfPreference) { Write-Host 'Preview only. Add -Delete to remove eligible files permanently.'; return }
if (Build-Active $running) { throw 'Build/cook/import is active. Nothing deleted; rerun after it finishes.' }
$deletedBytes=0L; $deletedFiles=0; $outcomes=[Collections.Generic.List[object]]::new()
try {
    foreach ($row in $candidates) {
        if (!$PSCmdlet.ShouldProcess($row.Path,'Permanently delete inventoried old artifacts')) { continue }
        $null=Safe-Path $row.Path $row.Path
        $running=Process-List
        if (Build-Active $running) { throw 'A build/cook/import started; stopping cleanup.' }
        if (In-Use $row.Path $running) { $outcomes.Add(@{Path=$row.Path;Status='Skipped: now running'}); continue }
        $old=$inventories[$row.Path]; $fresh=Inventory $row.Path
        if ($fresh.Reason -or $fresh.Newest -ne $old.Newest -or $fresh.Files.Count -ne $old.Files.Count) {
            $outcomes.Add(@{Path=$row.Path;Status='Skipped: changed during preview'}); continue
        }
        $before=@{}; foreach ($file in $old.Files) { $before[$file.Path]=$file }
        if (@($fresh.Files | Where-Object { !$before.ContainsKey($_.Path) -or $before[$_.Path].Ticks -ne $_.Ticks -or $before[$_.Path].Created -ne $_.Created -or $before[$_.Path].Bytes -ne $_.Bytes }).Count) {
            $outcomes.Add(@{Path=$row.Path;Status='Skipped: files changed'}); continue
        }
        # Delete explicit files only; never recursively traverse a newly added link.
        foreach ($file in $old.Files) {
            $path=Safe-Path $file.Path $row.Path
            $now=Get-Item -LiteralPath $path -Force
            if ($now.PSIsContainer -or $now.Length -ne $file.Bytes -or $now.LastWriteTimeUtc.Ticks -ne $file.Ticks -or $now.CreationTimeUtc.Ticks -ne $file.Created) { throw "File changed: $path" }
            Remove-Item -LiteralPath $path -Force
            $deletedBytes+=$file.Bytes; $deletedFiles++
        }
        foreach ($dir in $old.Directories | Sort-Object Length -Descending) {
            $null=Safe-Path $dir $row.Path
            if (@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) { [IO.Directory]::Delete($dir,$false) }
        }
        $outcomes.Add(@{Path=$row.Path;Status='Deleted eligible files'})
    }
} finally {
    @{DeletedFiles=$deletedFiles;DeletedLogicalBytes=$deletedBytes;Outcomes=$outcomes.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$report/result.json"
    Write-Host ('Deleted {0} files / {1:N2} GiB logical bytes. Report: {2}' -f $deletedFiles,($deletedBytes/1GB),$report)
}
