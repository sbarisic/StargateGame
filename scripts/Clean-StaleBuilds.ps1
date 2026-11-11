#requires -Version 7.0
<#
.SYNOPSIS
Preview stale isolated .NET build outputs; use -Apply to delete them.
.DESCRIPTION
Only recognized bin/obj directories beneath artifacts are eligible. Source-tree
builds, packages, source assets, saves and evidence outside bin/obj are untouched.
Keeps the two newest output groups and anything written within two hours.
Refuses links and saved databases. Running programs protect their output group;
an active build/test/publish blocks application. No processes are stopped.
Examples:
  pwsh -File scripts/Clean-StaleBuilds.ps1
  pwsh -File scripts/Clean-StaleBuilds.ps1 -Apply
  pwsh -File scripts/Clean-StaleBuilds.ps1 -Protect artifacts/my-agent/build
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [string]$Repo='E:\Projects\StargateGame',
    [ValidateRange(0,100)][int]$KeepNewest=2,
    [ValidateRange(0,87600)][double]$MinAgeHours=2,
    [string[]]$Protect=@(),
    [switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$Repo=[IO.Path]::GetFullPath($Repo).TrimEnd('\','/')
if(!(Test-Path -LiteralPath "$Repo/src/StargateGame.Server/StargateGame.Server.csproj")) {throw 'Not a StargateGame checkout.'}
$art=[IO.Path]::GetFullPath((Join-Path $Repo 'artifacts'))
$cutoff=[DateTime]::UtcNow.AddHours(-$MinAgeHours)
$excluded=@('publish','client-package','galaxy','building-kit','settlement-meshes','native-source','reference-tools','JoltPhysicsSharp')
$protected=@($Protect | ForEach-Object {if([IO.Path]::IsPathRooted($_)){[IO.Path]::GetFullPath($_)}else{[IO.Path]::GetFullPath((Join-Path $Repo $_))}})
$report=Join-Path $Repo ('cleanup-reports/stale-builds-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))

function Under([string]$Path,[string]$Boundary) {
    $Path.Equals($Boundary,[StringComparison]::OrdinalIgnoreCase) -or $Path.StartsWith($Boundary.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)
}
function Assert-SafePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    if($full -eq $art -or !(Under $full $art)){throw "Outside isolated build boundary: $full"}
    for($cursor=$full;$cursor;$cursor=Split-Path $cursor -Parent){
        if(Test-Path -LiteralPath $cursor){
            if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Link retained: $cursor"}
        }
    }
    $full
}
function Scan([string]$Root) {
    $files=[Collections.Generic.List[object]]::new();$directories=[Collections.Generic.List[string]]::new()
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Root)
    $reason='';$newest=[DateTime]::MinValue
    while($pending.Count){
        $dir=$pending.Pop();$directories.Add($dir)
        $info=Get-Item -LiteralPath $dir -Force
        if($info.LastWriteTimeUtc -gt $newest){$newest=$info.LastWriteTimeUtc}
        foreach($item in Get-ChildItem -LiteralPath $dir -Force){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){$reason='Contains a link';continue}
            if($item.PSIsContainer){
                if($item.Name -match '^(saves?|worlds?|universes?|backups?)$'){$reason='Contains save directory'}
                $pending.Push($item.FullName);continue
            }
            if($item.Name -match '\.(sqlite|sqlite3|db)(-wal|-shm|-journal|\.bak)?$'){$reason='Contains saved database'}
            if($item.LastWriteTimeUtc -gt $newest){$newest=$item.LastWriteTimeUtc}
            $files.Add([pscustomobject]@{Path=$item.FullName;Bytes=$item.Length;Ticks=$item.LastWriteTimeUtc.Ticks})
        }
    }
    [pscustomobject]@{Files=$files;Directories=$directories;Newest=$newest;Reason=$reason}
}
function Output-Directories([string]$Root,[int]$Depth=0) {
    if($Depth -gt 3){return}
    foreach($dir in Get-ChildItem -LiteralPath $Root -Directory -Force){
        if($dir.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        if($Depth -eq 0 -and $dir.Name -in $excluded){continue}
        if($dir.Name -in @('bin','obj')){$dir;continue}
        if($dir.Name -match '^(assets|saves?|data|packages|publish|\.git)$'){continue}
        Output-Directories $dir.FullName ($Depth+1)
    }
}
function Processes { @(Get-CimInstance Win32_Process -ErrorAction Stop) }
function Build-Running($Processes) {
    # Reused MSBuild worker nodes are idle-capable, so only the invocation blocks.
    @($Processes | Where-Object {
        ($_.Name -match '^(dotnet|MSBuild)\.exe$' -and $_.CommandLine -and
         $_.CommandLine -notmatch '(?i)[-/]nodemode[:=]1\b' -and
         ($_.CommandLine -match '(?i)dotnet(?:\.exe)?"?\s+(build|publish|test|run|restore|msbuild)\b|\bMSBuild\.dll\b' -or $_.Name -eq 'MSBuild.exe'))
    }).Count -gt 0
}
function In-Use([string]$Group,$Processes) {
    $relative=[IO.Path]::GetRelativePath($Repo,$Group).Replace('/','\')
    foreach($p in $Processes){
        if($p.ExecutablePath -and (Under $p.ExecutablePath $Group)){return $true}
        $command=([string]$p.CommandLine).Replace('/','\')
        if($command.IndexOf($Group,[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $command.IndexOf($relative,[StringComparison]::OrdinalIgnoreCase) -ge 0){return $true}
        if($p.Name -like 'StargateGame*' -and !$p.ExecutablePath -and !$p.CommandLine){return $true}
    }
    $false
}

$running=Processes
$rows=[Collections.Generic.List[object]]::new();$inventories=@{}
foreach($dir in Output-Directories $art){
    $safe=Assert-SafePath $dir.FullName
    $scan=Scan $safe
    # A name alone is not enough to authorize removal of an arbitrary folder.
    $recognized=if($dir.Name -eq 'obj'){@($scan.Files | Where-Object {$_.Path.EndsWith('\project.assets.json',[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0}else{@($scan.Files | Where-Object {[IO.Path]::GetFileName($_.Path) -match '^(StargateGame\..*|FishGfx\..*)\.deps\.json$'}).Count -gt 0}
    $reason=$scan.Reason
    if(!$recognized){$reason='No recognized .NET build marker'}
    $group=Split-Path $safe -Parent
    if(@($protected | Where-Object {(Under $group $_) -or (Under $_ $group)}).Count){$reason='Explicitly protected'}
    if(In-Use $group $running){$reason='Running process uses this build'}
    $sum=($scan.Files | Measure-Object Bytes -Sum).Sum
    $row=[pscustomobject]@{Path=$safe;Group=$group;Bytes=[long]$sum;Files=$scan.Files.Count;NewestUtc=$scan.Newest;Action='Keep';Reason=$reason}
    $rows.Add($row);$inventories[$safe]=$scan
}
$newestGroups=@($rows | Group-Object Group | Sort-Object @{Expression={($_.Group.NewestUtc | Sort-Object -Descending | Select-Object -First 1)};Descending=$true} | Select-Object -First $KeepNewest -ExpandProperty Name)
foreach($row in $rows){
    # Protect both bin and obj if either half of the output group is recent.
    $groupNewest=($rows | Where-Object Group -eq $row.Group | Sort-Object NewestUtc -Descending | Select-Object -First 1).NewestUtc
    if(!$row.Reason -and $row.Group -in $newestGroups){$row.Reason='One of the newest build groups'}
    if(!$row.Reason -and $groupNewest -gt $cutoff){$row.Reason='Recently written'}
    if(!$row.Reason){$row.Action='Delete';$row.Reason='Stale generated output'}
}
New-Item -ItemType Directory -Path $report -Force -WhatIf:$false | Out-Null
$rows | Export-Csv -LiteralPath "$report/inventory.csv" -NoTypeInformation -WhatIf:$false
$candidates=@($rows | Where-Object Action -eq 'Delete')
$candidates | ForEach-Object {$inventories[$_.Path].Files} | Export-Csv -LiteralPath "$report/files.csv" -NoTypeInformation -WhatIf:$false
$bytes=[long](($candidates | Measure-Object Bytes -Sum).Sum)
$rows | Sort-Object Bytes -Descending | Select-Object Action,@{n='GiB';e={[math]::Round($_.Bytes/1GB,2)}},Path,Reason | Format-Table -AutoSize
Write-Host ("Eligible: {0:N2} GiB in {1} directories (logical bytes). Report: {2}" -f ($bytes/1GB),$candidates.Count,$report)
if(!$Apply -or $WhatIfPreference){Write-Host 'Preview only. Rerun with -Apply to remove eligible build outputs.';return}
if(Build-Running $running){throw 'A build/test/publish invocation is active. Nothing deleted; rerun after it finishes.'}

$deletedBytes=0L;$deletedFiles=0;$outcomes=[Collections.Generic.List[object]]::new()
try {
    foreach($row in $candidates){
        if(!$PSCmdlet.ShouldProcess($row.Path,'Delete inventoried stale build files')){continue}
        $safe=Assert-SafePath $row.Path
        $running=Processes
        if(Build-Running $running){throw 'A build/test/publish started; stopping cleanup.'}
        if(In-Use $row.Group $running){$outcomes.Add(@{Path=$safe;Status='Skipped: now in use'});continue}
        $old=$inventories[$safe];$fresh=Scan $safe
        if($fresh.Reason -or $fresh.Newest -ne $old.Newest -or $fresh.Files.Count -ne $old.Files.Count){$outcomes.Add(@{Path=$safe;Status='Skipped: changed since inventory'});continue}
        $before=@{};foreach($file in $old.Files){$before[$file.Path]=$file}
        $changed=@($fresh.Files | Where-Object {!$before.ContainsKey($_.Path) -or $before[$_.Path].Bytes -ne $_.Bytes -or $before[$_.Path].Ticks -ne $_.Ticks})
        if($changed.Count){$outcomes.Add(@{Path=$safe;Status='Skipped: changed file'});continue}
        # No recursive delete and no shell handoff: only inventoried files, with
        # boundary/link/metadata revalidation. New or changed files are retained.
        foreach($file in $old.Files){
            $path=Assert-SafePath $file.Path
            $now=Get-Item -LiteralPath $path -Force
            if($now.Length -ne $file.Bytes -or $now.LastWriteTimeUtc.Ticks -ne $file.Ticks){throw "File changed; stopping cleanup: $path"}
            Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            $deletedBytes+=$file.Bytes;$deletedFiles++
        }
        foreach($directory in $old.Directories | Sort-Object Length -Descending){
            $null=Assert-SafePath $directory
            if(@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0){[IO.Directory]::Delete($directory,$false)}
        }
        $outcomes.Add(@{Path=$safe;Status='Deleted'})
    }
} finally {
    @{DeletedFiles=$deletedFiles;DeletedLogicalBytes=$deletedBytes;Outcomes=$outcomes.ToArray()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$report/result.json"
    Write-Host ("Deleted {0} files, {1:N2} GiB logical bytes. Evidence and packages retained. Report: {2}" -f $deletedFiles,($deletedBytes/1GB),$report)
}
