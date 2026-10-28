# Validação ponta a ponta do caminho de deploy PostgreSQL (desenvolvimento local):
#  1) cria banco e aplica deploy/postgres/001_inicial.sql + 002_seguranca_rls.sql via psql
#  2) sobe a API como app_comercial (sem DDL), gera dados reais pela API
#  3) roda deploy/postgres/backup.ps1 e restore-test.ps1
# Uso: powershell -File dev/pg-e2e.ps1   (requer PostgreSQL local e dev/dev-secrets.local.json)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$bin = 'C:\Program Files\PostgreSQL\18\bin'
$env:PATH = "$bin;$env:PATH"
$env:PGHOST = 'localhost'; $env:PGUSER = 'postgres'; $env:PGPASSWORD = $s.PostgresSuperPassword
$db = 'comercial_e2e'
$appPw = 'app-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 12)

function Step($m) { Write-Output "==> $m" }
function Invoke-Psql([string[]]$a) { & psql.exe -v ON_ERROR_STOP=1 -q @a; if ($LASTEXITCODE -ne 0) { throw "psql falhou: $a" } }

Step 'Banco novo + migração + RLS (como no deploy)'
Invoke-Psql @('-d', 'postgres', '-c', "DROP DATABASE IF EXISTS $db WITH (FORCE)")
Invoke-Psql @('-d', 'postgres', '-c', "CREATE DATABASE $db")
Invoke-Psql @('-d', $db, '-f', (Join-Path $root 'deploy/postgres/001_inicial.sql'))
Invoke-Psql @('-d', $db, '-f', (Join-Path $root 'deploy/postgres/002_seguranca_rls.sql'))
# Senha como variável do psql (:'pw'), nunca concatenada no SQL.
"ALTER ROLE app_comercial PASSWORD :'pw';" | & psql.exe -d $db -X -q -v ON_ERROR_STOP=1 -v "pw=$appPw"
if ($LASTEXITCODE -ne 0) { throw 'psql falhou: ALTER ROLE' }

Step 'API como app_comercial'
$api = Start-Process dotnet -PassThru -WindowStyle Hidden -WorkingDirectory (Join-Path $root 'src/Comercial.Api') -RedirectStandardOutput "$env:TEMP\api-e2e.log" -RedirectStandardError "$env:TEMP\api-e2e.err" -ArgumentList @(
  'run', '--no-launch-profile', '--', '--environment', 'Development', '--urls', 'http://localhost:5090',
  '--Database:Provider=Postgres', "--ConnectionStrings:Main=Host=localhost;Database=$db;Username=app_comercial;Password=$appPw",
  "--Bootstrap:AdminEmail=$($s.AdminEmail)", "--Bootstrap:AdminPassword=$($s.AdminPassword)", "--Bootstrap:AdminTotpSecret=$($s.AdminTotpSecret)")
try {
  $base = 'http://localhost:5090'
  for ($i = 0; $i -lt 90; $i++) { try { Invoke-RestMethod "$base/health" | Out-Null; break } catch { Start-Sleep 1 } }
  if ($i -ge 90) { throw 'API não subiu. Veja %TEMP%\api-e2e.log' }

  Step 'Dados reais via API'
  $totp = node (Join-Path $PSScriptRoot 'totp.js')
  $admin = (Invoke-RestMethod "$base/admin/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{ email = $s.AdminEmail; password = $s.AdminPassword; totpCode = $totp } | ConvertTo-Json)).accessToken
  $ha = @{ Authorization = "Bearer $admin" }
  Invoke-RestMethod "$base/admin/v1/tenants" -Method Post -Headers $ha -ContentType 'application/json' -Body (@{
    code = 'e2e-loja'; legalName = 'E2E LTDA'; tradeName = 'E2E'; planCode = 'profissional'; storeName = 'Matriz'
    owner = @{ email = 'dono@e2e.test'; displayName = 'Dono'; password = 'Senha-Dono-123' } } | ConvertTo-Json) | Out-Null
  $owner = (Invoke-RestMethod "$base/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{ tenantCode = 'e2e-loja'; email = 'dono@e2e.test'; password = 'Senha-Dono-123' } | ConvertTo-Json)).accessToken
  $ho = @{ Authorization = "Bearer $owner" }
  $store = (Invoke-RestMethod "$base/api/v1/stores" -Headers $ho)[0].id
  $prod = (Invoke-RestMethod "$base/api/v1/products" -Method Post -Headers $ho -ContentType 'application/json' -Body (@{ sku = 'E1'; name = 'Produto E2E'; unit = 'UN'; priceCents = 1500; costCents = 900; minStock = 0 } | ConvertTo-Json)).id
  Invoke-RestMethod "$base/api/v1/stock/entries" -Method Post -Headers $ho -ContentType 'application/json' -Body (@{ storeId = $store; productId = $prod; quantity = 10; unitCostCents = 900; reason = 'carga' } | ConvertTo-Json) | Out-Null
  $cash = (Invoke-RestMethod "$base/api/v1/cash/sessions" -Method Post -Headers $ho -ContentType 'application/json' -Body (@{ storeId = $store; openingCents = 0 } | ConvertTo-Json)).id
  # Horário com fuso -03:00 (como um cliente no Brasil enviaria).
  $sale = Invoke-RestMethod "$base/api/v1/sales" -Method Post -Headers $ho -ContentType 'application/json' -Body (@{
    idempotencyKey = 'e2e-1'; storeId = $store; cashSessionId = $cash; localCreatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    items = @(@{ productId = $prod; quantity = 2 }); payments = @(@{ method = 1; amountCents = 3000 }) } | ConvertTo-Json -Depth 5)
  Write-Output "Venda $($sale.id) total=$($sale.totalCents)"
  $verify = Invoke-RestMethod "$base/admin/v1/audit/verify" -Headers $ha
  Write-Output "Auditoria íntegra: $($verify.ok) ($($verify.records) registros)"
}
finally {
  Stop-Process -Id $api.Id -Force -ErrorAction SilentlyContinue
  Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object { $_.CommandLine -match 'Comercial.Api' -and $_.CommandLine -match '5090' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
}

Step 'Backup (deploy/postgres/backup.ps1)'
$out = Join-Path $env:TEMP 'comercial-backups'
$sw = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $root 'deploy/postgres/backup.ps1') -Database $db -OutDir $out
$bkp = $sw.Elapsed.TotalSeconds
$dump = Get-ChildItem $out -Filter "$db-*.dump" | Sort-Object LastWriteTime | Select-Object -Last 1

Step 'Restauração (deploy/postgres/restore-test.ps1)'
$sw.Restart()
& (Join-Path $root 'deploy/postgres/restore-test.ps1') -DumpFile $dump.FullName
Write-Output ("Tempos: backup {0:F1}s, restauração+verificação {1:F1}s, dump {2} KB" -f $bkp, $sw.Elapsed.TotalSeconds, [math]::Round($dump.Length / 1KB))

Invoke-Psql @('-d', 'postgres', '-c', "DROP DATABASE IF EXISTS $db WITH (FORCE)")
Remove-Item $out -Recurse -Force
Step 'OK'
