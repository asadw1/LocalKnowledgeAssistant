<#
.SYNOPSIS
    Stops the local Ollama server, its model runners and the tray app if it is running.
#>
$ErrorActionPreference = 'Stop'

# 'ollama app' is the tray app, which restarts the server if left running; 'ollama' covers the server and its runners.
$processes = Get-Process -Name 'ollama app', 'ollama' -ErrorAction SilentlyContinue
if (-not $processes) {
    Write-Host 'Ollama is not running.'
    return
}

$processes | Stop-Process -Force
$processes | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
Write-Host "Stopped $($processes.Count) Ollama process(es)."
