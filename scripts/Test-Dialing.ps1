param([switch]$Failed,[string]$BuildRoot='',[switch]$NoBuild,[switch]$Json)
$recipe=if($Failed){'dialing-failure'}else{'gate-roundtrip'}
& "$PSScriptRoot/Test.ps1" run -Scenario travel -Recipe $recipe -BuildRoot $BuildRoot -NoBuild:$NoBuild -Json:$Json
