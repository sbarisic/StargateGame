param([string[]]$Presets=@('temperate'),[string[]]$Views=@('horizon-45'),
    [string]$Horizon='on',[string]$GateLighting='on',[string]$Waves='on',
    [switch]$Stress,[switch]$Occlusion,[switch]$Props,[switch]$Players,[switch]$Motion,[switch]$WaveCheck,[switch]$ShadowFixture,
    [ValidateSet('motion','all','stationary')][string]$PropMode='motion',[string]$VisualTime='',
    [Alias('Graphics')][string]$Quality='performance',[int]$Seconds=3,[string]$BuildRoot='',[switch]$NoBuild,[string]$OutputDirectory='')
$settings=@{horizon=($Horizon -eq 'on');gateLighting=($GateLighting -eq 'on');waves=($Waves -eq 'on');stress=[bool]$Stress;horizonOcclusion=[bool]$Occlusion;horizonProps=[bool]$Props;horizonPlayers=[bool]$Players;waveCheck=[bool]$WaveCheck;shadowFixture=[bool]$ShadowFixture;propMode=$PropMode}
if($VisualTime){$settings.visualTime=[double]::Parse($VisualTime,[Globalization.CultureInfo]::InvariantCulture)}
& "$PSScriptRoot/Test-Visual.ps1" -Family horizon -Presets $Presets -Views $Views -Quality $Quality -Seconds $Seconds -Motion:$Motion -BuildRoot $BuildRoot -NoBuild:$NoBuild -OutputDirectory $OutputDirectory -Settings $settings
