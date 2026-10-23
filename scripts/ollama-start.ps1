<#
.SYNOPSIS
    Starts the local Ollama server in the background and waits until it answers.
.DESCRIPTION
    Does nothing if Ollama is already reachable. Warns if the configured model is not pulled.
    Stop it again with scripts/ollama-stop.ps1.
#>
param(
    # 127.0.0.1, not localhost: Ollama listens on IPv4 only and Windows spends ~2s failing over from ::1.
    [string]$BaseUrl = 'http://127.0.0.1:11434',
    [string]$Model = 'qwen2.5:7b-instruct',
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Test-Ollama {
    try { Invoke-RestMethod "$BaseUrl/api/tags" -TimeoutSec 5 } catch { $null }
}

$tags = Test-Ollama
if ($tags) {
    Write-Host "Ollama is already running at $BaseUrl."
}
else {
    $exe = (Get-Command ollama -ErrorAction SilentlyContinue).Source
    if (-not $exe) { $exe = Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe' }
    if (-not (Test-Path $exe)) { throw "ollama.exe not found. Install it from https://ollama.com/download." }

    Write-Host "Starting $exe serve ..."
    Start-Process -FilePath $exe -ArgumentList 'serve' -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not ($tags = Test-Ollama)) {
        if ((Get-Date) -gt $deadline) { throw "Ollama did not answer at $BaseUrl within $TimeoutSeconds seconds." }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "Ollama is up at $BaseUrl."
}

if ($tags.models.name -notcontains $Model) {
    Write-Warning "Model '$Model' is not pulled. Run: ollama pull $Model"
}
else {
    Write-Host "Model '$Model' is available."
}
