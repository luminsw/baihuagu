<#
百花谷 Family 版 - Windows (PowerShell) 轻量 CLI
用法: .\bhg.ps1 [command]
  bhg.ps1                打开 dashboard
  bhg.ps1 setup          首次配置（交互）
  bhg.ps1 start          启动服务（在后台运行 dotnet run）
  bhg.ps1 stop           停止服务
  bhg.ps1 status         查看服务状态
  bhg.ps1 restart        重启服务
  bhg.ps1 logs <name>    查看日志（taskrunner, webui, ai, vault）
  bhg.ps1 open           打开 Web 管理界面 (http://localhost:5177)

说明:
- 该脚本为简易移植，依赖 PowerShell (推荐 pwsh) 和 dotnet SDK
- 后台进程 PID 与日志保存在 $env:TEMP\bhg-<service>.*
#>
param(
	[string]$Command = 'dashboard',
	[string]$Arg,
	[string]$Browser = ''
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest

function Get-BhgRoot {
	if ($PSScriptRoot) { return $PSScriptRoot }
	if ($MyInvocation -and $MyInvocation.MyCommand -and $MyInvocation.MyCommand.Path) {
		return Split-Path -Parent $MyInvocation.MyCommand.Path
	}
	return (Get-Location).Path
}

$BHG_ROOT = Get-BhgRoot
$TEMP_DIR = $env:TEMP

# 启动顺序：后端服务先启动，WebUI 最后启动
$ServiceOrder = @('ai', 'vault', 'taskrunner', 'webui')
$Services = @{ 
	ai         = "services/TaskRunner.AI";
	vault      = "services/TaskRunner.Vault";
	taskrunner = "services/TaskRunner.Family";
	webui      = "services/WebUI.Family";
}

# 服务健康检查 URL
$HealthUrls = @{
	ai         = 'http://127.0.0.1:8791/api/ai/config/providers'
	vault      = 'http://127.0.0.1:8790/api/settings/vault-root-path-preference'
	taskrunner = 'http://127.0.0.1:8788/api/capability'
	webui      = 'http://127.0.0.1:5177/login'
}

function Get-LogPath($name){ Join-Path $TEMP_DIR "bhg-$name.log" }
function Get-PidPath($name){ Join-Path $TEMP_DIR "bhg-$name.pid" }

function Start-ServiceProc($name, $projRelPath){
	$projPath = Join-Path $BHG_ROOT $projRelPath
	if (-not (Test-Path $projPath)){
		Write-Host "[!] 项目未找到: $projPath" -ForegroundColor Yellow
		return
	}
	$log = Get-LogPath $name
	$errLog = "$log.err"
	$pidFile = Get-PidPath $name

	if (Test-Path $pidFile) {
		$existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
		if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
			Write-Host "[INFO] $name is already running (PID $existingPid)"
			return
		} else {
			Remove-Item $pidFile -ErrorAction SilentlyContinue
		}
	}

	Write-Host "Starting $name -> $projPath"
	$args = @('run', '--project', "$projPath", '--no-launch-profile')

	try {
		$prevEnv = $env:ASPNETCORE_ENVIRONMENT
		$prevDataDir = $env:YJ_DATA_DIR
		$env:ASPNETCORE_ENVIRONMENT = 'Development'
		$env:YJ_DATA_DIR = Join-Path $BHG_ROOT 'data'
		$proc = Start-Process -FilePath 'dotnet' -ArgumentList $args -RedirectStandardOutput $log -RedirectStandardError $errLog -NoNewWindow -PassThru
		if ($null -ne $prevEnv) { $env:ASPNETCORE_ENVIRONMENT = $prevEnv } else { Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
		if ($null -ne $prevDataDir) { $env:YJ_DATA_DIR = $prevDataDir } else { Remove-Item Env:\YJ_DATA_DIR -ErrorAction SilentlyContinue }
		Start-Sleep -Milliseconds 200
		$procId = $proc.Id
		Set-Content -Path $pidFile -Value $procId
		Write-Host "Started $name (PID $procId), log: $log (stderr: $errLog)"
	} catch {
		Write-Host "ERROR: failed to start ${name}: ${_}"
	}
}

function Stop-ServiceProc($name){
	$pidFile = Get-PidPath $name
	if (-not (Test-Path $pidFile)){
		Write-Host "[i] $name 未运行 (无 PID 文件)" -ForegroundColor Yellow
		return
	}
	$existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
	if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
		try {
			Stop-Process -Id $existingPid -Force -ErrorAction Stop
			Remove-Item $pidFile -ErrorAction SilentlyContinue
			Write-Host "Stopped ${name} (PID $existingPid)"
		} catch {
			Write-Host "ERROR: failed to stop ${name}: ${_}"
		}
	} else {
		Remove-Item $pidFile -ErrorAction SilentlyContinue
		Write-Host "$name is not running (cleaned pidfile)"
	}
}

function Show-Status(){
	foreach ($k in $ServiceOrder){
		$pidFile = Get-PidPath $k
		$status = "$k : stopped" 
		$color = [ConsoleColor]::DarkYellow
		if (Test-Path $pidFile){
			$existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
			if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)){
				$healthUrl = $HealthUrls[$k]
				$healthy = $false
				if ($healthUrl) {
					try {
						$resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
						$healthy = $resp.StatusCode -ge 200 -and $resp.StatusCode -lt 400
					} catch { $healthy = $false }
				}
				if ($healthy) {
					$status = "$k : running (PID $existingPid) ✓ healthy"
					$color = [ConsoleColor]::Green
				} else {
					$status = "$k : running (PID $existingPid) ⚠ not ready"
					$color = [ConsoleColor]::Yellow
				}
			} else {
				$status = "$k : pidfile exists but process not found"
				$color = [ConsoleColor]::Yellow
			}
		}
		Write-Host $status -ForegroundColor $color
	}
}

function Tail-Log($name){
	$log = Get-LogPath $name
	$errLog = "$log.err"
	if (-not (Test-Path $log)) { Write-Host "Log not found: $log"; if (Test-Path $errLog){ Write-Host "But stderr exists: $errLog" }; return }
	Write-Host "Tailing log: $log (Ctrl+C to stop)"
	if (Test-Path $errLog) { Write-Host "Also monitoring stderr: $errLog" }
	Get-Content -Path $log -Tail 50 -Wait
}

function Cmd-Setup {
	Write-Host "*** 首次配置向导（简化）" -ForegroundColor Cyan
	$vault = Read-Host "Enter vault path (e.g. C:\Users\you\MyNotes)"
	if (-not [string]::IsNullOrWhiteSpace($vault)) {
		if (-not (Test-Path $vault)) { New-Item -ItemType Directory -Path $vault -Force | Out-Null; Write-Host "Created: $vault" }
		$cfgPath = Join-Path $BHG_ROOT 'local.config.json'
		$obj = @{ vault = $vault }
		$obj | ConvertTo-Json | Set-Content -Path $cfgPath -Encoding UTF8
		Write-Host "Saved config: $cfgPath"
	} else { Write-Host "Vault not set, abort." }
}

function Open-InBrowser([string]$url){
	if ($Browser) {
		Write-Host "Opening: $url (browser: $Browser)"
		try { Start-Process $Browser $url } catch { Write-Host "Cannot launch browser '${Browser}': ${_}" }
	} else {
		Write-Host "Opening: $url"
		try { Start-Process $url } catch { Write-Host "Cannot open browser: ${_}" }
	}
}

function Open-Dashboard {
	Open-InBrowser 'http://127.0.0.1:5177'
}

function Ensure-ServiceRunning($name){
	$pidFile = Get-PidPath $name
	$isRunning = $false
	if (Test-Path $pidFile){
		$existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
		if ($existingPid -and (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)){
			$isRunning = $true
		}
	}
	if (-not $isRunning){
		Write-Host "Service $name not running, starting..."
		Start-ServiceProc $name $Services[$name]
	} else {
		Write-Host "Service $name already running"
	}
}

function Wait-For-Url([string]$url, [int]$timeoutSec = 30){
	$sw = [System.Diagnostics.Stopwatch]::StartNew()
	while ($sw.Elapsed.TotalSeconds -lt $timeoutSec){
		try{
			$resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
			if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 400){
				return $true
			}
		} catch {
			# ignore and retry
		}
		Start-Sleep -Seconds 1
	}
	return $false
}

function Wait-For-Service([string]$name, [int]$timeoutSec = 45){
	$healthUrl = $HealthUrls[$name]
	if (-not $healthUrl) { 
		Write-Host "  $name : no health check URL, skipping wait"
		return $true 
	}
	Write-Host "  Waiting for $name to become ready..."
	$ok = Wait-For-Url $healthUrl $timeoutSec
	if ($ok) {
		Write-Host "  $name : ✓ ready" -ForegroundColor Green
	} else {
		Write-Host "  $name : ⚠ timeout after ${timeoutSec}s (may still be starting)" -ForegroundColor Yellow
	}
	return $ok
}

switch ($Command.ToLower()){
	'help' { Get-Help; break }
	'setup' { Cmd-Setup; break }
	'start' {
		foreach ($k in $ServiceOrder){ Start-ServiceProc $k $Services[$k] }
		break
	}
	'stop' {
		foreach ($k in $ServiceOrder){ Stop-ServiceProc $k }
		break
	}
	'restart' {
		foreach ($k in $ServiceOrder){ Stop-ServiceProc $k }
		Start-Sleep -Seconds 1
		foreach ($k in $ServiceOrder){ Start-ServiceProc $k $Services[$k] }
		break
	}
	'status' { Show-Status; break }
	'logs' {
		if (-not $Arg){ Write-Host "请指定服务名: taskrunner, webui, ai, vault" -ForegroundColor Yellow; break }
		Tail-Log $Arg; break
	}
	'open' { Open-Dashboard; break }
	'dashboard' {
		Write-Host "=== 百花谷 Dashboard ===" -ForegroundColor Cyan

		# 1. 启动所有后端服务（按顺序）
		Ensure-ServiceRunning 'ai'
		Ensure-ServiceRunning 'vault'
		Ensure-ServiceRunning 'taskrunner'
		Ensure-ServiceRunning 'webui'

		# 2. 等待后端服务就绪
		Write-Host ""
		Write-Host "Waiting for services to become ready..." -ForegroundColor Cyan
		$allReady = $true
		foreach ($name in @('ai', 'vault', 'taskrunner')) {
			if (-not (Wait-For-Service $name 45)) { $allReady = $false }
		}

		# 3. 等待 WebUI 就绪
		Write-Host "  Waiting for WebUI to become ready..."
		if (-not (Wait-For-Url 'http://127.0.0.1:5177/login' 30)){
			Write-Host "  WebUI : ⚠ timeout. Check logs: .\bhg.ps1 logs webui" -ForegroundColor Yellow
			break
		}
		Write-Host "  WebUI : ✓ ready" -ForegroundColor Green

		# 4. 获取 CLI token 并打开浏览器
		Write-Host ""
		try {
			$resp = Invoke-WebRequest -Uri 'http://127.0.0.1:5177/api/auth/cli-token' -Method POST -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
			$json = $resp.Content | ConvertFrom-Json
			$token = $json.token
			if ($token) {
				$dashboardUrl = "http://127.0.0.1:5177/?cli-token=$token"
				Write-Host "Opening dashboard with CLI token..."
				Open-InBrowser $dashboardUrl
			} else {
				Write-Host "Failed to get CLI token, opening without auto-login"
				Open-Dashboard
			}
		} catch {
			Write-Host "Failed to get CLI token: ${_}. Opening without auto-login"
			Open-Dashboard
		}

		if (-not $allReady) {
			Write-Host ""
			Write-Host "⚠ Some backend services are not ready yet. The page may show incomplete data." -ForegroundColor Yellow
			Write-Host "  Run '.\bhg.ps1 status' to check, or '.\bhg.ps1 logs <name>' for details." -ForegroundColor Yellow
		}
		break
	}
	default { Open-Dashboard }
}
