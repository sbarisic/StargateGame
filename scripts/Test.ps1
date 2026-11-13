param(
    [ValidateSet('list','run','start','status','wait','action','capture','stop','check','mcp','prepare','doctor','sessions','attach','detach','observe','operation','cancel','recipe','cleanup')][string]$Operation='check',
    [string]$Scenario='sandbox', [string]$Preset='default', [string]$Recipe='visual-check', [string[]]$Group=@('quick'),
    [string]$Id='', [string[]]$Sections=@(), [string]$Filter='', [int]$Offset=0, [int]$Limit=20, [int]$WaitSeconds=0, [switch]$Async, [switch]$Export, [switch]$RetainState, [switch]$Headless, [switch]$Delete,
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
$hostRoot=Join-Path $root 'artifacts/testing-tools'
$binary=Join-Path $hostRoot 'bin/StargateGame.TestHost/release/StargateGame.TestHost.dll'
Push-Location $root
try {
    $sources = @(Get-ChildItem "$root/src/StargateGame.TestHost","$root/src/StargateGame.Testing" -File | Where-Object Extension -in '.cs','.csproj')
    $needsHost = !(Test-Path -LiteralPath $binary) -or @($sources | Where-Object LastWriteTimeUtc -gt (Get-Item $binary -ErrorAction SilentlyContinue).LastWriteTimeUtc).Count -gt 0
    if ($needsHost -and !$NoBuild) {
        if (Test-Path -LiteralPath $binary) {
            try { $probe=[IO.File]::Open($binary,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None); $probe.Dispose() }
            catch { throw 'The test host needs rebuilding but is in use. Close its MCP connection before updating the host.' }
        }
        New-Item -ItemType Directory -Force $hostRoot | Out-Null
        & dotnet build "$root/src/StargateGame.TestHost" -c Release --artifacts-path $hostRoot --nologo *> (Join-Path $hostRoot 'bootstrap.log')
        if ($LASTEXITCODE) { throw "Test host build failed: $hostRoot/bootstrap.log" }
    }
    if (!(Test-Path -LiteralPath $binary)) { throw 'Test host missing. Run Test.ps1 list once without -NoBuild.' }
    $launch=@($binary,$Operation,'--build-root',$BuildRoot,'--repo-root',$root,'--scenario',$Scenario,'--preset',$Preset,'--recipe',$Recipe,
        '--group',($Group -join ','),'--clients',"$Clients",'--timeout',"$Timeout",'--seed',"$Seed",'--biome',$Biome,
        '--landscape',$Landscape,'--settlement',$Settlement,'--style',$Style,'--hour',$Hour.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--weather',$Weather,'--quality',$Quality,'--terrain-rate',"$TerrainRate",'--destination-delay',"$DestinationDelay")
    $explicitOverrides=@('seed','biome','landscape') | Where-Object { $PSBoundParameters.ContainsKey($_) }
    $launch+=@('--overrides',($explicitOverrides -join ','))
    if ($PSBoundParameters.ContainsKey('Client')) { $launch+=@('--client',"$Client") }
    if ($ContentCache) { $launch+=@('--content-cache',[IO.Path]::GetFullPath($ContentCache)) }
    if ($NoBuild) { $launch+='--no-build' }
    if ($RetainState) { $launch+='--retain-state' }
    if ($Headless) { $launch+='--headless' }
    if ($Async) { $launch+='--async' }
    if ($Export) { $launch+='--export' }
    if ($Delete) { $launch+='--delete' }
    if ($Id) { $launch+=@('--id',$Id) }
    if ($Sections) { $launch+=@('--sections',($Sections -join ',')) }
    $launch+=@('--offset',"$Offset",'--limit',"$Limit",'--wait-seconds',"$WaitSeconds",'--filter',$Filter)
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
