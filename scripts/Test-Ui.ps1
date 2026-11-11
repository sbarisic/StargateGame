param([string]$BuildRoot='', [switch]$NoBuild, [switch]$Live)
$options=@{BuildRoot=$BuildRoot;NoBuild=$NoBuild}
& "$PSScriptRoot/Test.ps1" check -Group ui,address-book,ui-render @options
if($Live){ & "$PSScriptRoot/Test.ps1" run -Scenario sandbox -Recipe ui-live -BuildRoot $BuildRoot -NoBuild }
