# Advanced wave/prop/character rendering lifecycle; shares coordinator ownership.
param([string]$BuildRoot='', [switch]$NoBuild)
& "$PSScriptRoot/Test.ps1" run -Scenario sandbox -Recipe horizon-lifecycle @PSBoundParameters
