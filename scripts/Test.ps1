param(
    [ValidateSet('list','run','start','status','wait','action','capture','stop','check')][string]$Operation='check',
    [string]$Scenario='sandbox', [string]$Preset='default', [string]$Recipe='visual-check', [string[]]$Group=@('quick'),
    [string]$Session='', [string]$Request='', [string]$Condition='captureReady', [string]$Name='capture.png',
    [string]$BuildRoot='', [string]$ContentCache='', [switch]$NoBuild, [switch]$Json, [switch]$KeepOpen,
    [int]$Clients=1, [int]$Client=1, [int]$Timeout=240, [int]$Seed=7319,
    [string]$Biome='temperate', [string]$Landscape='Inland', [string]$Settlement='wilderness', [string]$Style='auto',
    [double]$Hour=12, [string]$Weather='off', [string]$Quality='performance',
    [switch]$Cold, [switch]$Delayed, [int]$TerrainRate=0, [int]$DestinationDelay=0,
    [switch]$Building, [switch]$Streaming, [switch]$Stress, [switch]$Explore, [switch]$Slow
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (!$BuildRoot -and $Session) {
    if ($Session -notmatch '^[a-fA-F0-9]{32}$') { throw 'Invalid session ID' }
    $record=Join-Path $env:LOCALAPPDATA ('StargateGame/Testing/sessions/'+$Session+'/session.json')
    $BuildRoot=(Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).buildRoot
}
if (!$BuildRoot) { $BuildRoot=Join-Path $root 'artifacts/testing-build' }
$BuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if ($Building) { $Group='building' }
if ($Streaming -or $Stress -or $Explore -or $Slow) { $Group='streaming' }
$binary=Join-Path $BuildRoot 'bin/StargateGame.Harness/release/StargateGame.Harness.dll'
Push-Location $root
try {
    if (!$NoBuild -and $Operation -in @('list','run','start','check')) {
        $sessions=Join-Path $env:LOCALAPPDATA 'StargateGame/Testing/sessions'
        if (Test-Path $sessions) { foreach ($file in Get-ChildItem $sessions -Filter session.json -Recurse) {
            $active=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($active.buildRoot -eq $BuildRoot -and $active.state -in @('starting','ready','switching') -and (Get-Process -Id $active.coordinatorPid -ErrorAction SilentlyContinue)) { throw "Build is in use by session $($active.id). Stop it, use -NoBuild, or choose a different -BuildRoot." }
        } }
        New-Item -ItemType Directory -Force $BuildRoot | Out-Null
        $buildArgs=@('build','StargateGame.sln','-c','Release','--artifacts-path',$BuildRoot,'--nologo','-p:CookAssets=true')
        if (!$ContentCache) { $ContentCache=Join-Path $root 'artifacts/cooked' }
        $ContentCache=[IO.Path]::GetFullPath($ContentCache)
        $buildArgs += "-p:AssetCookCache=$ContentCache"
        $packs=Join-Path $ContentCache 'packs'
        if (Test-Path -LiteralPath (Join-Path $packs 'catalog.json')) { $buildArgs += "-p:CookedAssetPacks=$packs" }
        & dotnet @buildArgs *> (Join-Path $BuildRoot 'build.log')
        if ($LASTEXITCODE) { throw "Build failed: $BuildRoot/build.log" }
    }
    if (!(Test-Path -LiteralPath $binary)) {
        foreach ($candidate in @((Join-Path $BuildRoot 'StargateGame.Harness.dll'),(Join-Path $BuildRoot 'testing/StargateGame.Harness/StargateGame.Harness.dll'))) { if (Test-Path -LiteralPath $candidate) { $binary=$candidate; break } }
    }
    if (!(Test-Path -LiteralPath $binary)) { throw "Harness missing: $binary. Run without -NoBuild." }
    $launch=@($binary,$Operation,'--build-root',$BuildRoot,'--repo-root',$root,'--scenario',$Scenario,'--preset',$Preset,'--recipe',$Recipe,
        '--group',($Group -join ','),'--clients',"$Clients",'--client',"$Client",'--timeout',"$Timeout",'--seed',"$Seed",'--biome',$Biome,
        '--landscape',$Landscape,'--settlement',$Settlement,'--style',$Style,'--hour',$Hour.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--weather',$Weather,'--quality',$Quality,'--terrain-rate',"$TerrainRate",'--destination-delay',"$DestinationDelay")
    $explicitOverrides=@('seed','biome','landscape') | Where-Object { $PSBoundParameters.ContainsKey($_) }
    $launch+=@('--overrides',($explicitOverrides -join ','))
    if ($Session) { $launch+=@('--session',$Session) }
    if ($Request) { $launch+=@('--request',[IO.Path]::GetFullPath($Request)) }
    if ($Operation -eq 'wait') { $launch+=@('--condition',$Condition) }
    if ($Operation -eq 'capture') { $launch+=@('--name',$Name) }
    if ($KeepOpen) { $launch+='--keep-open' }
    if ($Cold) { $launch+='--cold' }
    if ($Delayed) { $launch+='--delayed' }
    if ($Stress) { $launch+='--stress' }
    if ($Explore) { $launch+='--explore' }
    if ($Slow) { $launch+='--slow' }
    if ($Json) { $launch+='--json' }
    & dotnet @launch
    if ($LASTEXITCODE) { throw "Harness operation failed ($LASTEXITCODE). See the returned artifact directory." }
} finally { Pop-Location }
