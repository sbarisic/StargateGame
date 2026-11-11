#requires -Version 7.0
# All destructive checks use a new temporary checkout, never real artifacts.
$ErrorActionPreference='Stop'
$cleaner=Join-Path $PSScriptRoot 'Clean-Artifacts.ps1'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('stargate-artifact-cleanup-'+[guid]::NewGuid().ToString('N'))
function Put([string]$Path,[string]$Text='fixture') {
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value $Text
}
function Check($Value,[string]$Message) { if(!$Value){throw "FAIL: $Message"}; Write-Host "PASS: $Message" }
function Age([string]$Path) {
    foreach($item in Get-ChildItem -LiteralPath $Path -Force) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        if($item.PSIsContainer){Age $item.FullName}
        $item.CreationTimeUtc=[DateTime]::UtcNow.AddDays(-3)
        $item.LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-3)
    }
    $item=Get-Item -LiteralPath $Path
    $item.CreationTimeUtc=[DateTime]::UtcNow.AddDays(-3)
    $item.LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-3)
}
Put "$fixture/src/StargateGame.Server/StargateGame.Server.csproj" '<Project/>'
Put "$fixture/src/StargateGame.Server/bin/old.dll"
Put "$fixture/src/StargateGame.Server/obj/project.assets.json"
Put "$fixture/src/StargateGame.Server/source.cs"
Put "$fixture/assets/source.png"
Put "$fixture/artifacts/validation/old/build/old.dll"
Put "$fixture/artifacts/validation/old/evidence.png"
Put "$fixture/artifacts/validation/old/saves/universe.db"
Put "$fixture/artifacts/validation/old/other/universe.sqlite-wal"
Put "$fixture/artifacts/validation/old/other/identity.json"
Put "$fixture/artifacts/validation/recent/build/old.dll"
Put "$fixture/artifacts/validation/running/build/game.exe"
Put "$fixture/artifacts/validation/protected/build/old.dll"
Put "$fixture/artifacts/asset-packs/duplicate/assets/old.sga"
Put "$fixture/artifacts/cooked/bin/old.dll"
Put "$fixture/artifacts/cooked/asset-cache/current.bin"
Put "$fixture/artifacts/cooked/packs/catalog.json"
Put "$fixture/artifacts/publish/client-win-x64/current.dll"
Put "$fixture/artifacts/publish/client-previous/old.dll"
Put "$fixture/artifacts/galaxy/galaxy.bin"
Put "$fixture/artifacts/native-source/source.cpp"
Put "$fixture/artifacts/linked/old.dll"
Put "$fixture/outside/keep.txt"
$null=New-Item -ItemType Junction -Path "$fixture/artifacts/linked/link" -Target "$fixture/outside"
Age $fixture
Put "$fixture/artifacts/validation/recent/build/old.dll" 'recent modification'
$global:ArtifactCleanupProcesses=@([pscustomobject]@{Name='StargateGame.Client.exe';ExecutablePath="$fixture/artifacts/validation/running/build/game.exe";CommandLine='game'})
function Get-CimInstance { param($ClassName,$ErrorAction) $global:ArtifactCleanupProcesses }
& $cleaner -Repo $fixture -Protect artifacts/validation/protected *> "$fixture/preview.log"
Check (Test-Path "$fixture/artifacts/validation/old/evidence.png") 'default preview preserves files'
& $cleaner -Repo $fixture -Protect artifacts/validation/protected -Delete -WhatIf *> "$fixture/whatif.log"
Check (Test-Path "$fixture/artifacts/validation/old/evidence.png") 'WhatIf preserves files'
& $cleaner -Repo $fixture -Protect artifacts/validation/protected -Delete *> "$fixture/delete.log"
foreach($relative in @('artifacts/validation/old/build/old.dll','artifacts/validation/old/evidence.png','artifacts/asset-packs/duplicate/assets/old.sga','artifacts/cooked/bin/old.dll','artifacts/publish/client-previous/old.dll','src/StargateGame.Server/bin/old.dll','src/StargateGame.Server/obj/project.assets.json')) {
    Check (!(Test-Path "$fixture/$relative")) "removed $relative"
}
foreach($relative in @('src/StargateGame.Server/source.cs','assets/source.png','artifacts/validation/old/saves/universe.db','artifacts/validation/old/other/universe.sqlite-wal','artifacts/validation/old/other/identity.json','artifacts/validation/recent/build/old.dll','artifacts/validation/running/build/game.exe','artifacts/validation/protected/build/old.dll','artifacts/cooked/asset-cache/current.bin','artifacts/cooked/packs/catalog.json','artifacts/publish/client-win-x64/current.dll','artifacts/galaxy/galaxy.bin','artifacts/native-source/source.cpp','artifacts/linked/old.dll','outside/keep.txt')) {
    Check (Test-Path "$fixture/$relative") "preserved $relative"
}
Put "$fixture/artifacts/blocked/file.bin"; Age "$fixture/artifacts/blocked"
$global:ArtifactCleanupProcesses=@([pscustomobject]@{Name='dotnet.exe';ExecutablePath='C:\Program Files\dotnet\dotnet.exe';CommandLine='dotnet build active.csproj'})
$blocked=$false
try { & $cleaner -Repo $fixture -Delete *> "$fixture/blocked.log" } catch { $blocked=$_.Exception.Message -like '*active*' }
Check ($blocked -and (Test-Path "$fixture/artifacts/blocked/file.bin")) 'active build blocks deletion'
Remove-Variable ArtifactCleanupProcesses -Scope Global
Write-Host "Fixture and reports: $fixture"
