# Audio assertions share the normal gate recipe and harness process ownership.
param([switch]$Failed,[string]$BuildRoot='',[switch]$NoBuild,[switch]$Json)
$recipe=if($Failed){'audio-failure'}else{'audio'}
& "$PSScriptRoot/Test.ps1" run -Scenario travel -Recipe $recipe -BuildRoot $BuildRoot -NoBuild:$NoBuild -Json:$Json
