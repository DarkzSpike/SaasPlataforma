# Monta a empresa de demonstração do zero (API local rodando): cria a empresa "loja-demo" se não existir,
# cadastra os produtos de exemplo, ativa todos os módulos implementados e publica o visual "Demo PDV".
# Outra empresa de teste: -TenantCode exemplo -OwnerEmail exemplo@gmail.com -OwnerPassword exemplo123 -TradeName "Exemplo"
param([string]$TenantCode, [string]$OwnerEmail, [string]$OwnerPassword, [string]$TradeName)
$ErrorActionPreference = 'Stop'
$s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $TenantCode) { $TenantCode = $TenantCode }
if (-not $OwnerEmail) { $OwnerEmail = $OwnerEmail }
if (-not $OwnerPassword) { $OwnerPassword = $OwnerPassword }
if (-not $TradeName) { $TradeName = 'Loja Demo' }
$base = 'http://localhost:5080'
$json = 'application/json; charset=utf-8'
function Body($o) { , [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Depth 6)) }

$admin = $null
foreach ($off in 0, 1) {
  try { $admin = (Invoke-RestMethod "$base/admin/v1/auth/login" -Method Post -ContentType $json -Body (Body @{ email = $s.AdminEmail; password = $s.AdminPassword; totpCode = (node (Join-Path $PSScriptRoot 'totp.js') $off) })).accessToken; break }
  catch { if ($off -eq 1) { throw "Login do admin falhou: $($_.Exception.Message)" } }
}
$h = @{ Authorization = "Bearer $admin" }
$exists = (Invoke-RestMethod "$base/admin/v1/tenants?q=$($TenantCode)" -Headers $h) | Where-Object { $_.code -eq $TenantCode }
if (-not $exists) {
  Invoke-RestMethod "$base/admin/v1/tenants" -Method Post -Headers $h -ContentType $json -Body (Body @{
    code = $TenantCode; legalName = $TradeName; tradeName = $TradeName; planCode = 'profissional'; storeName = 'Matriz'
    owner = @{ email = $OwnerEmail; displayName = $TradeName; password = $OwnerPassword } }) | Out-Null
  Write-Output "Empresa $($TenantCode) criada."
} else { Write-Output "Empresa $($TenantCode) já existe." }

# O código TOTP usado no login não pode ser reutilizado: aguarda o próximo passo antes do script de módulos.
Start-Sleep -Seconds (31 - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 30))
$a = @{ TenantCode = $TenantCode; OwnerEmail = $OwnerEmail; OwnerPassword = $OwnerPassword; TradeName = $TradeName }
& (Join-Path $PSScriptRoot 'seed-demo.ps1') @a
& (Join-Path $PSScriptRoot 'enable-demo.ps1') @a
