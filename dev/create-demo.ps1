# Monta a empresa de demonstração do zero (API local rodando): cria a empresa "loja-demo" se não existir,
# cadastra os produtos de exemplo, ativa todos os módulos implementados e publica o visual "Demo PDV".
$ErrorActionPreference = 'Stop'
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$base = 'http://localhost:5080'
$json = 'application/json; charset=utf-8'
function Body($o) { , [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Depth 6)) }

$admin = $null
foreach ($off in 0, 1) {
  try { $admin = (Invoke-RestMethod "$base/admin/v1/auth/login" -Method Post -ContentType $json -Body (Body @{ email = $s.AdminEmail; password = $s.AdminPassword; totpCode = (node (Join-Path $PSScriptRoot 'totp.js') $off) })).accessToken; break }
  catch { if ($off -eq 1) { throw "Login do admin falhou: $($_.Exception.Message)" } }
}
$h = @{ Authorization = "Bearer $admin" }
$exists = (Invoke-RestMethod "$base/admin/v1/tenants?q=$($s.DemoTenantCode)" -Headers $h) | Where-Object { $_.code -eq $s.DemoTenantCode }
if (-not $exists) {
  Invoke-RestMethod "$base/admin/v1/tenants" -Method Post -Headers $h -ContentType $json -Body (Body @{
    code = $s.DemoTenantCode; legalName = 'Loja Demo Variedades LTDA'; tradeName = 'Loja Demo'; planCode = 'profissional'; storeName = 'Matriz'
    owner = @{ email = $s.DemoOwnerEmail; displayName = 'Dona Demo'; password = $s.DemoOwnerPassword } }) | Out-Null
  Write-Output "Empresa $($s.DemoTenantCode) criada."
} else { Write-Output "Empresa $($s.DemoTenantCode) já existe." }

# O código TOTP usado no login não pode ser reutilizado: aguarda o próximo passo antes do script de módulos.
Start-Sleep -Seconds (31 - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 30))
& (Join-Path $PSScriptRoot 'seed-demo.ps1')
& (Join-Path $PSScriptRoot 'enable-demo.ps1')
