param([string]$BuildRoot='', [Alias('SkipBuild')][switch]$NoBuild, [int]$StartCase=0, [int]$CaseCount=1,
    [string[]]$Views=@('settlement','settlement-above'), [float]$TimeOfDay=12)
$root=Split-Path $PSScriptRoot -Parent
if(!$BuildRoot){$BuildRoot=Join-Path $root 'artifacts/testing-build'}
& "$PSScriptRoot/Test.ps1" check -Group settlement-seeds -BuildRoot $BuildRoot -NoBuild:$NoBuild
$cases=Get-Content (Join-Path $root 'artifacts/settlement-inspection.json') -Raw | ConvertFrom-Json
foreach($case in ($cases | Select-Object -Skip $StartCase -First $CaseCount)) {
    $steps=@($Views | ForEach-Object { @{action='camera';data=@{bookmark=$_}}; @{action='capture';data=@{name="$_.png"}} })
    $recipe=Join-Path $BuildRoot ('settlement-'+[guid]::NewGuid().ToString('N')+'.json')
    @{name='settlement';steps=$steps} | ConvertTo-Json -Depth 12 | Set-Content $recipe
    & "$PSScriptRoot/Test.ps1" run -Scenario planet-explorer -Biome $case.preset -Seed $case.seed -Settlement $case.type -Style $case.style -Hour $TimeOfDay -Recipe $recipe -BuildRoot $BuildRoot -NoBuild
}
