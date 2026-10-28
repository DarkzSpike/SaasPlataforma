# Sobe a API em modo desenvolvimento.
# Padrão: PostgreSQL local (banco comercial_dev). A cada início aplica os scripts IDEMPOTENTES de deploy
# (001_inicial.sql + 002_seguranca_rls.sql) como superusuário e roda a API como app_comercial (com RLS),
# então mudanças de schema NÃO apagam os dados da demo.
# Escuta em todas as interfaces (*:5080, IPv4 e IPv6) para o app do celular conectar pela rede local (mesmo Wi-Fi).
# Use -Sqlite para o modo antigo (arquivo dev-data/comercial-dev.db, recriado quando o schema muda).
param([switch]$Sqlite)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$common = @("--Bootstrap:AdminEmail=$($s.AdminEmail)", "--Bootstrap:AdminPassword=$($s.AdminPassword)", "--Bootstrap:AdminTotpSecret=$($s.AdminTotpSecret)",
            "--Payments:generic:WebhookSecret=$($s.WebhookSecret)")
Set-Location (Join-Path $root 'src/Comercial.Api')

# Uma API já aberta (outro terminal) trava as DLLs e a porta 5080: encerra antes de compilar.
$old = Get-Process Comercial.Api -ErrorAction SilentlyContinue
if ($old) {
  Write-Host "Encerrando API que ja estava rodando (PID $($old.Id -join ', '))..."
  $old | Stop-Process -Force
  $old | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if ($Sqlite) {
  dotnet run --no-launch-profile -- --environment Development --urls http://*:5080 @common
  exit
}

$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$env:PGHOST = 'localhost'; $env:PGUSER = 'postgres'; $env:PGPASSWORD = $s.PostgresSuperPassword
$env:PGOPTIONS = '-c client_min_messages=warning'   # esconde avisos "já existe, ignorando" dos scripts idempotentes
$db = 'comercial_dev'
if (-not (& $psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$db'")) { & $psql -d postgres -q -c "CREATE DATABASE $db" }
& $psql -d $db -q -v ON_ERROR_STOP=1 -f (Join-Path $root 'deploy/postgres/001_inicial.sql') | Out-Null
& $psql -d $db -q -v ON_ERROR_STOP=1 -f (Join-Path $root 'deploy/postgres/002_seguranca_rls.sql') | Out-Null
# Senha como VARIÁVEL do psql (:'pw' é citada pelo próprio psql): nunca colar segredo no texto do SQL.
"ALTER ROLE app_comercial PASSWORD :'pw';" | & $psql -d $db -X -q -v ON_ERROR_STOP=1 -v "pw=$($s.AppDbPassword)" | Out-Null
Remove-Item Env:PGPASSWORD, Env:PGOPTIONS

dotnet run --no-launch-profile -- --environment Development --urls http://*:5080 `
  --Database:Provider=Postgres "--ConnectionStrings:Main=Host=localhost;Database=$db;Username=app_comercial;Password=$($s.AppDbPassword)" @common
