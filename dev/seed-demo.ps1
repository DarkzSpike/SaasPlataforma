# Popula a empresa de demonstração (API local em http://localhost:5080) com produtos e estoque.
# Idempotente: produtos já existentes (mesmo SKU) são mantidos. Credenciais em dev/dev-secrets.local.json.
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
# A vírgula impede o PowerShell de "desenrolar" o byte[] em object[] (o corpo iria corrompido).
function Body($o) { , [Text.Encoding]::UTF8.GetBytes(($o | ConvertTo-Json -Depth 5)) }

$token = (Invoke-RestMethod "$base/api/v1/auth/login" -Method Post -ContentType $json -Body (Body @{ tenantCode = $TenantCode; email = $OwnerEmail; password = $OwnerPassword })).accessToken
$h = @{ Authorization = "Bearer $token" }
$store = (Invoke-RestMethod "$base/api/v1/stores" -Headers $h)[0].id
$existing = @(Invoke-RestMethod "$base/api/v1/products" -Headers $h) | ForEach-Object { $_.sku }

$catalogo = @(
  @{ sku = 'CAN001'; barcode = '7891000100011'; name = 'Caneca de cerâmica 300 ml'; priceCents = 2490; costCents = 1100; qty = 40 },
  @{ sku = 'CAD002'; barcode = '7891000100028'; name = 'Caderno universitário 10 matérias'; priceCents = 2990; costCents = 1650; qty = 25 },
  @{ sku = 'PIL003'; barcode = '7891000100035'; name = 'Pilha alcalina AA (par)'; priceCents = 1190; costCents = 600; qty = 60 },
  @{ sku = 'TOA004'; barcode = '7891000100042'; name = 'Toalha de rosto algodão'; priceCents = 1990; costCents = 900; qty = 3 },
  @{ sku = 'COP005'; barcode = '7891000100059'; name = 'Copo de vidro 350 ml'; priceCents = 690; costCents = 280; qty = 120 },
  @{ sku = 'ORG006'; barcode = '7891000100066'; name = 'Caixa organizadora 20 L'; priceCents = 4590; costCents = 2300; qty = 12 }
)
foreach ($p in $catalogo) {
  if ($existing -contains $p.sku) { Write-Output "já existe: $($p.name)"; continue }
  $prod = Invoke-RestMethod "$base/api/v1/products" -Method Post -Headers $h -ContentType $json -Body (Body @{
    sku = $p.sku; barcode = $p.barcode; name = $p.name; unit = 'UN'; priceCents = $p.priceCents; costCents = $p.costCents; minStock = 5 })
  Invoke-RestMethod "$base/api/v1/stock/entries" -Method Post -Headers $h -ContentType $json -Body (Body @{
    storeId = $store; productId = $prod.id; quantity = $p.qty; unitCostCents = $p.costCents; reason = 'carga inicial (demonstração)' }) | Out-Null
  Write-Output "criado: $($p.name) ($($p.qty) un.)"
}
