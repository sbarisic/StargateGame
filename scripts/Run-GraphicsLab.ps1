param([ValidateSet('graphics-lab','gate-pair')][string]$Scene='graphics-lab',[ValidateSet('temperate','desert','icy')][string]$Biome='temperate',[ValidateSet('low','performance','high')][string]$Quality='performance',[ValidateRange(1,2)][int]$Clients=1,[string]$Build='artifacts/character-implementation/build',[int]$Seconds=0,[string]$View='player',[int]$Characters=1,[ValidateRange(0,24)][double]$TimeOfDay=12,[string[]]$ClientArguments=@(),[switch]$Rebuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
$owned=@()
try {
 if($Rebuild){dotnet build src/StargateGame.Client -c Release --artifacts-path $Build; if($LASTEXITCODE){throw 'Client build failed'};dotnet build src/StargateGame.Server -c Release --artifacts-path $Build;if($LASTEXITCODE){throw 'Server build failed'}}
 $out=Join-Path $root ('artifacts/graphics-lab/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+$Scene);New-Item -ItemType Directory -Path $out -Force | Out-Null
 $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
 $bin=Join-Path ([IO.Path]::GetFullPath($Build)) 'bin';$timer=[Diagnostics.Stopwatch]::StartNew()
 $server=Start-Process dotnet -WindowStyle Hidden -PassThru -ArgumentList @((Join-Path $bin 'StargateGame.Server/release/StargateGame.Server.dll'),'--bind','127.0.0.1','--port',"$port",'--fixture',$Scene,'--preset',$Biome,'--allow-test-identities') -RedirectStandardOutput "$out/server.log" -RedirectStandardError "$out/server-error.log";$owned+=$server
 while(-not ((Get-Content "$out/server.log" -Raw -ErrorAction SilentlyContinue)-match 'Listening')){if($server.HasExited){throw "Server exited; see $out"};if($timer.Elapsed.TotalSeconds -gt 30){throw 'Server startup timeout'};Start-Sleep -Milliseconds 50}
 $serverReady=$timer.Elapsed.TotalMilliseconds;$players=@()
 for($i=0;$i -lt $Clients;$i++){
  $clientArgs=@((Join-Path $bin 'StargateGame.Client/release/StargateGame.Client.dll'),'--host','127.0.0.1','--port',"$port",'--name',"Lab-$i",'--identity-profile',"Lab-$i",'--fixture',$Scene,'--preset',$Biome,'--graphics',$Quality,'--vsync','off','--characters',"$Characters",'--view',$View,'--time-of-day',$TimeOfDay.ToString([Globalization.CultureInfo]::InvariantCulture),'--capture-dir',$out)
  $clientArgs+=$ClientArguments
  if($Seconds -gt 0){$clientArgs+=@('--benchmark-seconds',"$Seconds",'--benchmark-warmup','5')}
  $player=Start-Process dotnet -WindowStyle Hidden -PassThru -ArgumentList $clientArgs -RedirectStandardOutput "$out/client-$i.log" -RedirectStandardError "$out/client-$i-error.log";$owned+=$player;$players+=$player
 }
 Write-Host "Graphics lab artifacts: $out"
 while(@($players|Where-Object {-not $_.HasExited}).Count){Start-Sleep -Milliseconds 200}
 @{scene=$Scene;biome=$Biome;serverReadyMs=$serverReady;durationMs=$timer.Elapsed.TotalMilliseconds;clients=$Clients;port=$port}|ConvertTo-Json|Set-Content "$out/run.json"
 foreach($player in $players){$player.WaitForExit();if($null -ne $player.ExitCode -and $player.ExitCode -ne 0){throw "Client failed; see $out"}}
} finally {foreach($p in $owned){if(-not $p.HasExited){Stop-Process -Id $p.Id}};Pop-Location}
