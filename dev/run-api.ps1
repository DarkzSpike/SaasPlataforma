# Sobe a API em modo desenvolvimento (SQLite local), lendo segredos de dev/dev-secrets.local.json.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw | ConvertFrom-Json
Set-Location (Join-Path $root 'src/Comercial.Api')
dotnet run --no-launch-profile -- --environment Development --urls http://localhost:5080 `
  "--Bootstrap:AdminEmail=$($s.AdminEmail)" "--Bootstrap:AdminPassword=$($s.AdminPassword)" "--Bootstrap:AdminTotpSecret=$($s.AdminTotpSecret)" `
  "--Payments:generic:WebhookSecret=$($s.WebhookSecret)"
