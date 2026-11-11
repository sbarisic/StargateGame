param(
    [ValidateSet('graphics','environment','atmosphere','landscape','vegetation','forest','ground-mist','terrain-relief','horizon','dialing','audio','lifecycle')][string]$Family='graphics',
    [string[]]$Presets=@('temperate'), [string[]]$Views=@(), [double[]]$Hours=@(12),
    [Alias('Graphics')][string]$Quality='performance', [string]$Weather='off',
    [string]$Grass='on', [string]$GroundMist='on', [string]$Clouds='on', [string]$Volumetrics='on', [string]$Reflections='on',
    [int]$Seconds=3, [switch]$Motion, [switch]$FullMatrix, [string]$BuildRoot='', [switch]$NoBuild,
    [string]$OutputDirectory='', [hashtable]$Settings=@{}
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) { $OutputDirectory=Join-Path $root ('artifacts/testing/'+$Family+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$scenario=if($Family -in @('forest','vegetation','horizon','terrain-relief','dialing','audio','lifecycle')){'sandbox'}else{'planet-explorer'}
if (!$Views.Count) {
    $Views=@(switch($Family) {
        'forest' { 'forest-spawn' }
        'vegetation' { 'grass' }
        'atmosphere' { 'sun' }
        'ground-mist' { 'weather-shelter' }
        'terrain-relief' { 'grass' }
        'horizon' { 'horizon-45' }
        default { if ($scenario -eq 'sandbox') { 'gate-pair' } else { 'gate' } }
    })
}
if ($FullMatrix) { $Presets=@('temperate','icy','desert'); if($Family -eq 'atmosphere'){$Hours=@(0,6.2,12,17.8,18.5)} }
if ($Family -eq 'ground-mist') { $Presets=@('icy'); $Weather='light-snow' }
$steps=[Collections.Generic.List[object]]::new()
function Step([string]$Action,[hashtable]$Data) { $steps.Add(@{action=$Action;data=$Data;timeoutSeconds=240}) }
$first=$true
foreach($biome in $Presets) {
    if (!$first) { Step 'switch-scenario' @{scenario=$scenario;biome=$biome} }; $first=$false
    foreach($hour in $Hours) {
        $resolvedSettings=@{hour=$hour;quality=$Quality;weather=$Weather;grass=($Grass -eq 'on');groundMist=($GroundMist -eq 'on');clouds=($Clouds -eq 'on');volumetrics=($Volumetrics -eq 'on');reflections=($Reflections -eq 'on')}
        foreach($key in $Settings.Keys){$resolvedSettings[$key]=$Settings[$key]}
        if($Family -eq 'ground-mist'){$resolvedSettings.weatherFixture=$true;$resolvedSettings.weatherCheck=$true}
        Step 'settings' $resolvedSettings
        if($Family -eq 'horizon'){Step 'gate-preview' @{phase='open'}}
        foreach($view in $Views) {
            Step 'camera' @{bookmark=$view}
            if($Family -eq 'terrain-relief') {
                foreach($depth in @($false,$true)) { Step 'settings' @{parallax=$depth;meadowMaterial='rocky-rugged-terrain-pbr';grassDensity=18}; Step 'capture' @{name="relief-$depth.png"} }
                Step 'settings' @{parallax=$true;grassDensity=24}; Step 'capture' @{name='relief-denser.png'}
            } else { Step 'capture' @{name="$biome-$view-$hour.png"} }
            if($Motion) { for($frame=0;$frame -lt 8;$frame++){Step 'sample' @{seconds=0.15};Step 'capture' @{name="$biome-$view-$hour-motion-$frame.png"}} }
            Step 'sample' @{seconds=$Seconds}
            if($Family -eq 'ground-mist') { Step 'settings' @{roof=$false}; Step 'capture' @{name="$biome-$view-$hour-roof-off.png"}; Step 'settings' @{roof=$true} }
        }
    }
}
$recipe=Join-Path $output 'recipe.json'
@{name=$Family;steps=$steps.ToArray()} | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $recipe
$options=@{Scenario=$scenario;Biome=$Presets[0];Recipe=$recipe;NoBuild=$NoBuild;Json=$true}
if($BuildRoot){$options.BuildRoot=$BuildRoot}
if($Family -eq 'lifecycle'){$options.Recipe='lifecycle'}
if($Family -in @('dialing','audio')){$options.Recipe=if($Family -eq 'audio'){'audio'}else{'gate-roundtrip'}}
& "$PSScriptRoot/Test.ps1" run @options | Tee-Object -FilePath (Join-Path $output 'result.json')
