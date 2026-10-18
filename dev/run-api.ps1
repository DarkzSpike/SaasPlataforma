# Sobe a API em modo desenvolvimento.
# Padrão: PostgreSQL local (banco comercial_dev). A cada início aplica os scripts IDEMPOTENTES de deploy
# (001_inicial.sql + 002_seguranca_rls.sql) como superusuário e roda a API como app_comercial (com RLS),
# então mudanças de schema NÃO apagam os dados da demo.
# Use -Sqlite para o modo antigo (arquivo dev-data/comercial-dev.db, recriado quando o schema muda).
param([switch]$Sqlite)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$common = @("--Bootstrap:AdminEmail=$($s.AdminEmail)", "--Bootstrap:AdminPassword=$($s.AdminPassword)", "--Bootstrap:AdminTotpSecret=$($s.AdminTotpSecret)",
            "--Payments:generic:WebhookSecret=$($s.WebhookSecret)")
Set-Location (Join-Path $root 'src/Comercial.Api')

if ($Sqlite) {
  dotnet run --no-launch-profile -- --environment Development --urls http://localhost:5080 @common
  exit
}

$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$env:PGHOST = 'localhost'; $env:PGUSER = 'postgres'; $env:PGPASSWORD = $s.PostgresSuperPassword
$db = 'comercial_dev'
if (-not (& $psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$db'")) { & $psql -d postgres -q -c "CREATE DATABASE $db" }
& $psql -d $db -q -v ON_ERROR_STOP=1 -f (Join-Path $root 'deploy/postgres/001_inicial.sql') | Out-Null
& $psql -d $db -q -v ON_ERROR_STOP=1 -f (Join-Path $root 'deploy/postgres/002_seguranca_rls.sql') | Out-Null
& $psql -d $db -q -c "ALTER ROLE app_comercial PASSWORD '$($s.AppDbPassword)'" | Out-Null
Remove-Item Env:PGPASSWORD

dotnet run --no-launch-profile -- --environment Development --urls http://localhost:5080 `
  --Database:Provider=Postgres "--ConnectionStrings:Main=Host=localhost;Database=$db;Username=app_comercial;Password=$($s.AppDbPassword)" @common
