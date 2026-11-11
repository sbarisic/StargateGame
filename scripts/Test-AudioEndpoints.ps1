param([string]$BuildRoot='',[switch]$NoBuild,[switch]$Json)
& "$PSScriptRoot/Test.ps1" run -Scenario travel -Recipe audio-endpoints -Clients 2 -BuildRoot $BuildRoot -NoBuild:$NoBuild -Json:$Json
