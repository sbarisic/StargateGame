param([string]$BuildRoot='', [switch]$NoBuild)
& "$PSScriptRoot/Test.ps1" run -Scenario sandbox -Recipe movement-visual @PSBoundParameters
