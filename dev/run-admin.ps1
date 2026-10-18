# Sobe o painel do desenvolvedor (arquivos estáticos) apontando para a API local.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location (Join-Path $root 'src/Comercial.AdminWeb')
dotnet run --no-launch-profile -- --environment Development --urls http://localhost:5180
