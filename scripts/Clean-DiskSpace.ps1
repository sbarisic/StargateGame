#requires -Version 7.0
<#
.SYNOPSIS
Preview, or apply with -Apply, the approved Stargate disk cleanup.
.DESCRIPTION
Preserves releases, catalogue, saves, importer inputs, source and licenses.
Moves the local source-asset library to D: with hash verification and a junction.
Packs Git without rewriting history, pruning, or expiring reflogs.
Reports are written outside the cleanup roots. No process is terminated.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$Repo='E:\Projects\StargateGame',
    [string]$AssetWorkspace='D:\Games\Stargate Network',
    [switch]$Apply,
    [switch]$SkipAssetMove,
    [switch]$SkipGitMaintenance
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
# WhatIf uses the same manifest-only path as the default invocation.
if($WhatIfPreference) {$Apply=$false; $WhatIfPreference=$false}
$Repo=[IO.Path]::GetFullPath($Repo).TrimEnd('\')
$AssetWorkspace=[IO.Path]::GetFullPath($AssetWorkspace).TrimEnd('\')
$art=Join-Path $Repo 'artifacts'
$extraction=Join-Path $AssetWorkspace 'extraction'
$source=Join-Path $Repo 'assets_itch'
$destination=Join-Path $AssetWorkspace 'AssetSources/StargateGame/assets_itch'
$backup=Join-Path $Repo 'assets_itch.cleanup-source'
if (!(Test-Path -LiteralPath "$Repo/scripts/Publish.ps1") -or !(Test-Path -LiteralPath "$Repo/.git" -PathType Container)) { throw 'Not the expected StargateGame checkout.' }
$report=Join-Path $AssetWorkspace ('cleanup-reports/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $report | Out-Null

function Assert-Path([string]$Path,[string]$Boundary) {
    $full=[IO.Path]::GetFullPath($Path)
    $bound=[IO.Path]::GetFullPath($Boundary).TrimEnd('\')
    if ($full -ne $bound -and !$full.StartsWith($bound+'\',[StringComparison]::OrdinalIgnoreCase)) { throw "Outside boundary: $full" }
    $cursor=$full
    while($cursor) {
        if(Test-Path -LiteralPath $cursor) {
            $item=Get-Item -LiteralPath $cursor -Force
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected junction/link: $cursor" }
        }
        $cursor=Split-Path $cursor -Parent
    }
    return $full
}
function Tree([string]$Root) {
    foreach($item in Get-ChildItem -LiteralPath $Root -Force) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $item
        if($item.PSIsContainer) { Tree $item.FullName }
    }
}
function Under([string]$Path,[string]$Root) { return $Path -eq $Root -or $Path.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) }
function Fingerprints($Files) {
    foreach($file in $Files) { [pscustomobject]@{Path=$file.FullName;Bytes=$file.Length;Hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash} }
}
function Verify-Fingerprints($Records) {
    foreach($entry in $Records) {
        if(!(Test-Path -LiteralPath $entry.Path -PathType Leaf) -or (Get-FileHash -LiteralPath $entry.Path).Hash -ne $entry.Hash) { throw "Preserved file changed: $($entry.Path)" }
    }
}
function Remove-Empty($Directories,[string]$Boundary) {
    foreach($dir in ($Directories | Sort-Object @{Expression={$_.Length};Descending=$true},@{Expression={$_}} -Unique)) {
        try {
            if(!(Test-Path -LiteralPath $dir -PathType Container)) { continue }
            $safe=Assert-Path $dir $Boundary
            if(@(Get-ChildItem -LiteralPath $safe -Force).Count -eq 0) { [IO.Directory]::Delete($safe,$false) }
        } catch { Write-Warning "Kept directory ${dir}: $_" }
    }
}
function Relevant-Processes {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(dotnet|MSBuild|StargateGame.*|blender|python.*|cmake|ninja)\.exe$' -and
        $_.CommandLine -and ($_.CommandLine.Contains($Repo) -or $_.CommandLine.Contains($AssetWorkspace) -or $_.CommandLine -match 'StargateGame')
    })
}
function Build-Active($Processes) {
    return @($Processes | Where-Object {$_.Name -match '^(MSBuild|cmake|ninja)\.exe$' -or $_.CommandLine -match '(?i)dotnet(?:\.exe)?"?\s+(build|publish|test|run|restore|msbuild)\b'}).Count -gt 0
}
function Import-Active($Processes) {
    return @($Processes | Where-Object {$_.Name -match '^(blender|python.*)\.exe$'}).Count -gt 0
}
function Active-Roots($Processes) {
    foreach($process in $Processes) {
        foreach($match in [regex]::Matches($process.CommandLine,[regex]::Escape($Repo)+'[\\/](artifacts|src|tests|tools|thirdparty|vendor)[\\/]([^\\/"\s]+)',[Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            Join-Path (Join-Path $Repo $match.Groups[1].Value) $match.Groups[2].Value
        }
    }
}
$beforeDrives=@(Get-PSDrive D,E | Select-Object Name,Free)
$beforeDrives | ConvertTo-Json | Set-Content "$report/drives-before.json"
$null=Assert-Path $art $Repo
$null=Assert-Path $extraction $AssetWorkspace
$artifactTree=@(Tree $art)
$extractionTree=@(Tree $extraction)
$protectedRoots=[Collections.Generic.List[string]]::new()
foreach($name in @('publish','galaxy','building-kit','settlement-meshes')) { $protectedRoots.Add((Join-Path $art $name)) }
foreach($item in $artifactTree) {
    if($item.PSIsContainer -and $item.Name -match '(?i)(^|[-_])(saves?|saved|worlds?|universes?|backups?)([-_]|$)|^data$') { $protectedRoots.Add($item.FullName) }
    if(!$item.PSIsContainer -and $item.Name -match '(?i)\.(sqlite|sqlite3|db)([-.].*)?$') { $protectedRoots.Add($item.DirectoryName) }
}
$protectedSet=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($root in $protectedRoots) { $null=$protectedSet.Add($root) }
function Protected([string]$Path) {
    $cursor=$Path
    while($cursor.Length -ge $art.Length) {
        if($protectedSet.Contains($cursor)) {return $true}
        $index=$cursor.LastIndexOf('\')
        if($index -lt 0) {break}
        $cursor=$cursor.Substring(0,$index)
    }
    return $false
}
$candidates=[Collections.Generic.List[object]]::new()
function Candidate($File,[string]$Group,[string]$Boundary,[string]$OutputRoot) {
    $candidates.Add([pscustomobject]@{Path=$File.FullName;Bytes=$File.Length;WrittenUtc=$File.LastWriteTimeUtc;Group=$Group;Boundary=$Boundary;OutputRoot=$OutputRoot})
}
$processes=@(Relevant-Processes)
$busy=Build-Active $processes
$activeRoots=@(Active-Roots $processes | Sort-Object -Unique)
foreach($root in $activeRoots) {$null=$protectedSet.Add($root)}
if($activeRoots.Count) {Write-Host 'Active work retained:'; $activeRoots | ForEach-Object {Write-Host "  $_"}}
$recent=[DateTime]::UtcNow.AddMinutes(-2)
$busyOutputRoots=[Collections.Generic.List[string]]::new()
foreach($dir in ($artifactTree | Where-Object {$_.PSIsContainer -and $_.Name -match '^(build|.*-build|bin|obj)$'})) {
    $children=@($artifactTree | Where-Object {!$_.PSIsContainer -and (Under $_.FullName $dir.FullName)})
    if($busy -or @($children | Where-Object {$_.LastWriteTimeUtc -gt $recent}).Count) { $busyOutputRoots.Add($dir.FullName) }
}
foreach($file in $artifactTree) {
    if($file.PSIsContainer -or (Protected $file.FullName)) { continue }
    # Keep unique diagnostic source, project documents, licenses and provenance.
    if($file.Extension -in @('.cs','.csproj','.sln','.py','.ps1','.sh','.md') -or $file.Name -match '(?i)license|provenance|third-party|attribution') { continue }
    if($file.Name -match '(?i)\.(dpapi|pem|key)$') { continue }
    if($file.LastWriteTimeUtc -gt $recent) { continue }
    $blocked=$false
    foreach($root in $busyOutputRoots) { if(Under $file.FullName $root) { $blocked=$true; break } }
    if(!$blocked) { Candidate $file 'Artifacts/research' $art $art }
}

# Repository bin/obj only beneath project-bearing trees, never source assets.
$buildRoots=[Collections.Generic.List[string]]::new()
foreach($name in @('src','tests','tools','thirdparty','vendor','native')) {
    $area=Join-Path $Repo $name
    if(!(Test-Path -LiteralPath $area)) { continue }
    foreach($dir in (Tree $area | Where-Object {$_.PSIsContainer -and $_.Name -in @('bin','obj')})) {
        if(@(Get-ChildItem -LiteralPath $dir.Parent.FullName -Filter '*.csproj' -File).Count -eq 0) { continue }
        if(@($buildRoots | Where-Object {Under $dir.FullName $_}).Count) { continue }
        $items=@(Tree $dir.FullName)
        if($busy -or @($activeRoots | Where-Object {Under $dir.FullName $_}).Count -or @($items | Where-Object {!$_.PSIsContainer -and $_.LastWriteTimeUtc -gt $recent}).Count) { Write-Warning "Active/recent build retained: $($dir.FullName)"; continue }
        $buildRoots.Add($dir.FullName)
        $saveParents=@($items | Where-Object {!$_.PSIsContainer -and $_.Name -match '(?i)\.(sqlite|sqlite3|db)([-.].*)?$'} | ForEach-Object {$_.DirectoryName})
        foreach($file in $items) {
            if($file.PSIsContainer -or $file.FullName -match '(?i)\\(saves?|data|worlds?|universes?|backups?)\\|\.(dpapi|pem|key)$') { continue }
            if(@($saveParents | Where-Object {Under $file.FullName $_}).Count) { continue }
            Candidate $file 'Repository builds' $dir.FullName $dir.FullName
        }
    }
}

# Research previews only: never recursively treat all image files as disposable.
$previewRoots=@('building_library/renders','building_library/sheets','building_library/thumbnails','building_library/browser-check-profile',
    'terrain_library/rock_previews','terrain_library/lod_test','tree_optimization/renders',
    'megakit_preview','fortification_preview','game_tree_grid','new_trees_preview','unused_tree_preview','itch_review') | ForEach-Object {Join-Path $extraction $_}
foreach($file in $extractionTree) {
    if($file.PSIsContainer -or $file.LastWriteTimeUtc -gt $recent) { continue }
    $selected=$false
    if($file.Extension -match '^\.blend[0-9]+$') {
        $primary=$file.FullName -replace '(\.blend)[0-9]+$','$1'
        $selected=Test-Path -LiteralPath $primary -PathType Leaf
    }
    if($file.Extension -in @('.png','.jpg','.jpeg','.webp','.mp4','.webm','.log') -and
        ($file.DirectoryName -eq $extraction -or @($previewRoots | Where-Object {Under $file.FullName $_}).Count)) {
        if($file.Name -notmatch '(?i)texture|albedo|normal|roughness|metallic|emission|mask') { $selected=$true }
    }
    if($file.FullName -match '(?i)\\(__pycache__|browser-check-profile)\\') { $selected=$true }
    if($selected) { Candidate $file 'Extraction previews' $extraction $file.DirectoryName }
}

# Only remove this known convenience zip if every entry matches a loose file.
$archive=Join-Path $extraction 'tree_optimization/optimized-trees-glb.zip'
if(Test-Path -LiteralPath $archive) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $matches=0; $verified=$true
        foreach($entry in $zip.Entries) {
            if(!$entry.Name) { continue }
            if($entry.FullName -match '(^|[/\\])\.\.([/\\]|$)|^[\\/]|:') { $verified=$false; break }
            $loose=Join-Path (Split-Path $archive -Parent) $entry.FullName
            if(!(Test-Path -LiteralPath $loose -PathType Leaf)) { $verified=$false; break }
            $null=Assert-Path $loose (Split-Path $archive -Parent)
            if((Get-Item -LiteralPath $loose).Length -ne $entry.Length) { $verified=$false; break }
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try {$hash=[Convert]::ToHexString($sha.ComputeHash($stream))} finally {$stream.Dispose();$sha.Dispose()}
            if($hash -ne (Get-FileHash -LiteralPath $loose).Hash) {$verified=$false; break}
            $matches++
        }
        if($verified -and $matches -gt 0) { Candidate (Get-Item -LiteralPath $archive) 'Verified duplicate archive' $extraction (Split-Path $archive -Parent) }
        else { Write-Host 'Retaining tree archive: exact extracted duplicates were not all found.' }
    } finally {$zip.Dispose()}
}
$candidates=@($candidates | Sort-Object Path -Unique)
$candidates | Export-Csv "$report/deletions.csv" -NoTypeInformation
$candidates | Group-Object Group | ForEach-Object { [pscustomobject]@{Action=$_.Name;GiB=[math]::Round(($_.Group | Measure-Object Bytes -Sum).Sum/1GB,3);Files=$_.Count} } | Format-Table -AutoSize
$relocate=$false
if(!$SkipAssetMove -and (Test-Path -LiteralPath $source)) {
    $s=Get-Item -LiteralPath $source -Force
    if($s.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if($s.LinkType -ne 'Junction' -or [IO.Path]::GetFullPath([string]$s.Target).TrimEnd('\') -ne $destination.TrimEnd('\')) { throw 'Unexpected assets_itch link.' }
        Write-Host 'Source assets already relocated.'
    } elseif(!(Import-Active $processes)) {
        $relocate=$true
        $sourceTree=@(Tree $source)
        $sourceFiles=@($sourceTree | Where-Object {!$_.PSIsContainer})
        Write-Host ('Relocate E: -> D: {0:N3} GiB, {1} files' -f (($sourceFiles | Measure-Object Length -Sum).Sum/1GB),$sourceFiles.Count)
    }
}
Write-Host "Report: $report"
if(!$Apply) { Write-Host 'PREVIEW ONLY. Add -Apply to perform cleanup, verified relocation and Git packing.'; return }
if(!( $PSCmdlet.ShouldProcess($Repo,'Apply inventoried cleanup, relocate source assets and pack Git'))) { return }
if(Build-Active @(Relevant-Processes)) { throw 'A build started. Rerun after it finishes; nothing has been deleted.' }

Write-Host 'Hashing preserved packages, catalogue and saves...'
$keptFiles=@($artifactTree | Where-Object {
    $path=$_.FullName
    !$_.PSIsContainer -and (Protected $path) -and @($activeRoots | Where-Object {Under $path $_}).Count -eq 0
})
$kept=@(Fingerprints $keptFiles)
$kept | Export-Csv "$report/preserved.csv" -NoTypeInformation
$beforeRefs=@(& git -C $Repo show-ref)
if($LASTEXITCODE -ne 0) { throw 'Cannot read Git refs.' }
$beforeRefs | Set-Content "$report/git-refs-before.txt"
& git -C $Repo status --porcelain=v1 -uno | Set-Content "$report/git-status-before.txt"

if($relocate) {
    $null=Assert-Path $source $Repo
    $null=Assert-Path $destination $AssetWorkspace
    $null=Assert-Path $backup $Repo
    if(Test-Path -LiteralPath $backup) {throw 'Recovery copy already exists; inspect it before retrying asset relocation.'}
    if(@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) {throw 'Asset sources contain links; refusing relocation.'}
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $assetRecords=[Collections.Generic.List[object]]::new(); $n=0
    foreach($file in $sourceFiles) {
        $relative=$file.FullName.Substring($source.Length+1)
        $target=Join-Path $destination $relative
        $null=Assert-Path $target $destination
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        $hash=(Get-FileHash -LiteralPath $file.FullName).Hash
        if(Test-Path -LiteralPath $target) {if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw "Conflicting destination asset: $target"}}
        else {Copy-Item -LiteralPath $file.FullName -Destination $target}
        if((Get-Item -LiteralPath $target).Length -ne $file.Length -or (Get-FileHash -LiteralPath $target).Hash -ne $hash) {throw "Asset verification failed: $relative"}
        $assetRecords.Add([pscustomobject]@{Relative=$relative;Bytes=$file.Length;Hash=$hash})
        $n++; if($n%500 -eq 0) {Write-Host "Copied and verified $n / $($sourceFiles.Count) assets"}
    }
    $assetRecords | Export-Csv "$report/relocated-assets.csv" -NoTypeInformation
    $current=@(Tree $source | Where-Object {!$_.PSIsContainer})
    if($current.Count -ne $sourceFiles.Count -or (Import-Active @(Relevant-Processes))) {throw 'Assets/import processes changed during copy; source retained.'}
    foreach($entry in $assetRecords) {if((Get-FileHash -LiteralPath (Join-Path $source $entry.Relative)).Hash -ne $entry.Hash){throw 'Source changed during copy; source retained.'}}
    foreach($dir in ($sourceTree | Where-Object {$_.PSIsContainer})) {New-Item -ItemType Directory -Path (Join-Path $destination $dir.FullName.Substring($source.Length+1)) -Force | Out-Null}
    # Checked same-volume rename, then create the replacement before deleting duplicates.
    $null=Assert-Path $source $Repo; $null=Assert-Path $backup $Repo
    # Rename-Item performs one directory rename; Move-Item can move children
    # individually and leave a partial tree when a read-only child is encountered.
    $renamed=$false
    try {Rename-Item -LiteralPath $source -NewName (Split-Path $backup -Leaf) -Force; $renamed=$true}
    catch {
        if(Test-Path -LiteralPath $backup) {throw}
        Write-Warning 'Directory rename unavailable; will convert the verified, emptied original directory to a junction.'
    }
    if($renamed) {
        try {New-Item -ItemType Junction -Path $source -Target $destination | Out-Null}
        catch {Rename-Item -LiteralPath $backup -NewName (Split-Path $source -Leaf) -Force; throw}
    }
    $duplicateRoot=if($renamed){$backup}else{$source}
    try {
        foreach($entry in $assetRecords) {
            $old=Assert-Path (Join-Path $duplicateRoot $entry.Relative) $duplicateRoot
            if((Get-FileHash -LiteralPath $old).Hash -ne $entry.Hash) {throw 'Recovery copy changed; leaving remaining files intact.'}
            Remove-Item -LiteralPath $old -Force
        }
        $emptyDirectories=@(Tree $duplicateRoot | Where-Object {$_.PSIsContainer} | ForEach-Object {$_.FullName})
        if($renamed){$emptyDirectories+=@($backup)}
        Remove-Empty $emptyDirectories $duplicateRoot
        if(!$renamed) {
            $remaining=@(Get-ChildItem -LiteralPath $source -Force)
            if($remaining.Count){throw ('Original asset directory is not empty: '+($remaining.Name -join ', '))}
            New-Item -ItemType Junction -Path $source -Target $destination -Force | Out-Null
        }
    } catch {
        # Restore missing files after any failure during in-place conversion.
        # A successful rename already leaves importers using the verified D: copy.
        if(!$renamed -and !((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            foreach($entry in $assetRecords){
                $restore=Join-Path $source $entry.Relative
                New-Item -ItemType Directory -Path (Split-Path $restore -Parent) -Force | Out-Null
                if(!(Test-Path -LiteralPath $restore)) {Copy-Item -LiteralPath (Join-Path $destination $entry.Relative) -Destination $restore -Force}
            }
        }
        throw
    }
    foreach($entry in $assetRecords) {if((Get-FileHash -LiteralPath (Join-Path $source $entry.Relative)).Hash -ne $entry.Hash){throw 'Junction verification failed.'}}
    Write-Host 'Asset junction verified; original duplicate removed.'
}

$deleted=[Collections.Generic.List[object]]::new();$skipped=[Collections.Generic.List[object]]::new();$n=0
foreach($file in $candidates) {
    if($n%3000 -eq 0) {
        $running=@(Relevant-Processes)
        if(Build-Active $running) {throw 'A build started; leave remaining cleanup for a later run.'}
        $liveRoots=@(Active-Roots $running)
    }
    try {
        if(@($liveRoots | Where-Object {Under $file.Path $_}).Count) {throw 'Currently used by a running process.'}
        if(!(Test-Path -LiteralPath $file.Path)) {continue}
        $safe=Assert-Path $file.Path $file.Boundary
        $now=Get-Item -LiteralPath $safe -Force
        if($now.PSIsContainer -or $now.Length -ne $file.Bytes -or $now.LastWriteTimeUtc -ne $file.WrittenUtc) {throw 'Changed since inventory.'}
        Remove-Item -LiteralPath $safe -Force
        $deleted.Add($file)
    } catch {$skipped.Add([pscustomobject]@{Path=$file.Path;Reason=$_.Exception.Message})}
    $n++;if($n%3000 -eq 0){Write-Host "Processed $n / $($candidates.Count) cleanup files"}
}
$deleted | Export-Csv "$report/deleted.csv" -NoTypeInformation
$skipped | Export-Csv "$report/skipped.csv" -NoTypeInformation
Remove-Empty @($artifactTree | Where-Object {$_.PSIsContainer -and !(Protected $_.FullName)} | ForEach-Object {$_.FullName}) $art
foreach($root in $buildRoots) {if(Test-Path -LiteralPath $root){Remove-Empty @((Tree $root | Where-Object {$_.PSIsContainer} | ForEach-Object {$_.FullName});$root) $root}}

Verify-Fingerprints $kept
Write-Host 'Preserved package/catalogue/save hashes verified.'
if(!$SkipGitMaintenance) {
    & git -C $Repo count-objects -vH | Set-Content "$report/git-before.txt"
    & git -C $Repo -c gc.reflogExpire=never -c gc.reflogExpireUnreachable=never gc --no-prune *> "$report/git-gc.log"
    if($LASTEXITCODE -ne 0) {throw 'Git packing failed; inspect report.'}
    & git -C $Repo count-objects -vH | Set-Content "$report/git-after.txt"
}
$afterRefs=@(& git -C $Repo show-ref)
if(Compare-Object $beforeRefs $afterRefs) {throw 'Git refs changed during cleanup; inspect concurrent work.'}
& git -C $Repo fsck --connectivity-only *> "$report/git-connectivity.log"
if($LASTEXITCODE -ne 0) {throw 'Git connectivity check failed.'}
$afterDrives=@(Get-PSDrive D,E | Select-Object Name,Free)
$afterDrives | ConvertTo-Json | Set-Content "$report/drives-after.json"
$totals=@($deleted | Group-Object Group | ForEach-Object {[pscustomobject]@{Group=$_.Name;Bytes=($_.Group | Measure-Object Bytes -Sum).Sum;Files=$_.Count}})
@{Deleted=$totals;Skipped=$skipped.Count;Relocated=$relocate;Before=$beforeDrives;After=$afterDrives} | ConvertTo-Json -Depth 5 | Set-Content "$report/result.json"
$totals | Format-Table -AutoSize
foreach($drive in $afterDrives){$old=$beforeDrives | Where-Object Name -eq $drive.Name;Write-Host ('{0}: free-space change {1:N2} GiB; now {2:N2} GiB free' -f $drive.Name,(($drive.Free-$old.Free)/1GB),($drive.Free/1GB))}
Write-Host "Done; $($skipped.Count) skipped files. Report: $report"
