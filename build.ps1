# Builds CardboardBox.exe into the dist folder.
#   powershell -ExecutionPolicy Bypass -File build.ps1            (version from the .csproj)
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.2.0
param([string]$Version)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$props = @('-c', 'Release', '-nologo')
if ($Version) { $props += "-p:Version=$Version" }
dotnet build app/CardboardBox.csproj @props
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

New-Item -ItemType Directory -Force dist | Out-Null
Copy-Item app/bin/Release/net48/CardboardBox.exe dist/CardboardBox.exe -Force
$size = [math]::Round((Get-Item dist/CardboardBox.exe).Length / 1KB)
Write-Host "Built dist\CardboardBox.exe ($size KB)"
