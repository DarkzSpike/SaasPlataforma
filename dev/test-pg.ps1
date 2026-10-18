# Roda a suíte completa de testes contra o PostgreSQL local (bancos de teste criados por classe).
# Usa PostgresTestConnection de dev-secrets.local.json e as ferramentas pg_dump/pg_restore da instalação padrão.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$env:COMERCIAL_TEST_PG = $s.PostgresTestConnection
$env:COMERCIAL_TEST_PG_BIN = 'C:\Program Files\PostgreSQL\18\bin'
dotnet test (Join-Path $root 'tests/Comercial.Tests')
