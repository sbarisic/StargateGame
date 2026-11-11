#requires -Version 7.0
# Destructive behavior is exercised only in a newly created temporary fixture.
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot 'Clean-StaleBuilds.ps1'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('stargate-cleanup-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$fixture/src/StargateGame.Server" -Force | Out-Null
Set-Content "$fixture/src/StargateGame.Server/StargateGame.Server.csproj" '<Project/>'
function Make-Build([string]$Name){
    $root="$fixture/artifacts/$Name/build"
    New-Item -ItemType Directory -Path "$root/bin","$root/obj" -Force | Out-Null
    Set-Content "$root/bin/StargateGame.Client.deps.json" '{}'
    Set-Content "$root/bin/StargateGame.Client.dll" 'rebuildable test content'
    Set-Content "$root/obj/project.assets.json" '{}'
    $root
}
function Check($Value,[string]$Message){if(!$Value){throw "FAIL: $Message"};Write-Host "PASS: $Message"}
$stale=Make-Build 'stale';$recent=Make-Build 'recent';$keep=Make-Build 'explicit';$busy=Make-Build 'running';$save=Make-Build 'save';$link=Make-Build 'link'
Set-Content "$save/bin/universe.sqlite-wal" 'preserve database sidecar'
Set-Content "$fixture/artifacts/stale/evidence.mp4" 'preserve recording'
New-Item -ItemType Directory "$fixture/artifacts/publish/client/bin" -Force | Out-Null
Set-Content "$fixture/artifacts/publish/client/bin/StargateGame.Client.deps.json" '{}'
New-Item -ItemType Directory "$fixture/artifacts/unknown/bin" -Force | Out-Null
Set-Content "$fixture/artifacts/unknown/bin/source.txt" 'not generated'
New-Item -ItemType Directory "$fixture/outside" -Force | Out-Null
Set-Content "$fixture/outside/keep.txt" 'junction target'
$null=New-Item -ItemType Junction -Path "$link/bin/linked-assets" -Target "$fixture/outside"
# Do not recurse through the junction while aging the fixture.
function Age([string]$Path){foreach($item in Get-ChildItem -LiteralPath $Path -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){continue};if($item.PSIsContainer){Age $item.FullName};$item.LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-3)};(Get-Item -LiteralPath $Path).LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-3)}
Age "$fixture/artifacts"
Set-Content "$recent/bin/StargateGame.Client.dll" 'recent build'
$newest=Make-Build 'newest'
$global:StargateCleanupTestProcesses=@([pscustomobject]@{Name='StargateGame.Client.exe';ExecutablePath="$busy/bin/StargateGame.Client.exe";CommandLine="$busy/bin/StargateGame.Client.exe"})
# Deterministic process inventory applies only inside this fixture test process.
function Get-CimInstance {param($ClassName,$ErrorAction) $global:StargateCleanupTestProcesses}
& $script -Repo $fixture -KeepNewest 1 -Protect $keep *> "$fixture/preview.log"
Check (Test-Path "$stale/bin/StargateGame.Client.dll") 'preview does not delete files'
& $script -Repo $fixture -KeepNewest 1 -Protect $keep -Apply -WhatIf *> "$fixture/whatif.log"
Check (Test-Path "$stale/bin/StargateGame.Client.dll") 'WhatIf does not delete files'
& $script -Repo $fixture -KeepNewest 1 -Protect $keep -Apply *> "$fixture/apply.log"
Check (!(Test-Path "$stale/bin") -and !(Test-Path "$stale/obj")) 'apply deletes only stale generated bin/obj'
foreach($root in @($recent,$newest,$keep,$busy,$save,$link)){Check (Test-Path "$root/bin/StargateGame.Client.dll") "preserves $([IO.Path]::GetRelativePath($fixture,$root))"}
Check ((Get-Content "$fixture/outside/keep.txt") -eq 'junction target') 'junction target untouched'
Check (Test-Path "$fixture/artifacts/stale/evidence.mp4") 'evidence outside build outputs retained'
Check (Test-Path "$fixture/artifacts/publish/client/bin/StargateGame.Client.deps.json") 'release package retained'
Check (Test-Path "$fixture/artifacts/unknown/bin/source.txt") 'unrecognized bin directory retained'
$blocked=Make-Build 'blocked';Age $blocked
$global:StargateCleanupTestProcesses=@([pscustomobject]@{Name='dotnet.exe';ExecutablePath='C:\Program Files\dotnet\dotnet.exe';CommandLine='dotnet build another-agent.csproj'})
$threw=$false;try{& $script -Repo $fixture -KeepNewest 1 -Apply *> "$fixture/blocked.log"}catch{$threw=$_.Exception.Message -like '*active*'}
Check ($threw -and (Test-Path "$blocked/bin/StargateGame.Client.dll")) 'active build blocks deletion'
Write-Host "Cleanup fixture evidence: $fixture"
Remove-Variable StargateCleanupTestProcesses -Scope Global

