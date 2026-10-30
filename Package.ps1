[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'Dashboard\Build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$output = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $output) { throw "Файл уже существует: $output" }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('svpserver-package-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path (Join-Path $temp 'Dashboard') -Force | Out-Null
    foreach ($name in @('bridge.py','config.example.json','README.md','LICENSE','Install.ps1')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $temp }
    Copy-Item -LiteralPath (Join-Path $root 'Dashboard\SVPServer.exe') -Destination (Join-Path $temp 'Dashboard')
    Compress-Archive -Path (Join-Path $temp '*') -DestinationPath $output
    Write-Host "Готово: $output"
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
