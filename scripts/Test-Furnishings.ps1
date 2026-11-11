param([string]$BuildRoot='', [switch]$NoBuild, [switch]$Render)
$groups=@('furnishings','settlements','settlement-network','furnishing-fixtures')
if($Render){$groups+='blender'}
& "$PSScriptRoot/Test.ps1" check -Group $groups -Delayed -BuildRoot $BuildRoot -NoBuild:$NoBuild
