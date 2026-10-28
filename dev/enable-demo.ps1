# Ativa na empresa de demonstração TODOS os módulos que existem de verdade (status Beta ou Disponível)
# e publica a identidade visual "Demo PDV". Módulos "Planejados" não são ativados: o servidor recusa,
# porque ainda não têm funcionalidade. Requer a API local rodando (dev/run-api.ps1).
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
function Totp([int]$offset = 0) { node (Join-Path $PSScriptRoot 'totp.js') $offset }

# Login do admin (o código TOTP não pode ser reutilizado: tenta o próximo passo se o atual já foi usado).
$admin = $null
foreach ($off in 0, 1) {
  try { $admin = (Invoke-RestMethod "$base/admin/v1/auth/login" -Method Post -ContentType $json -Body (Body @{ email = $s.AdminEmail; password = $s.AdminPassword; totpCode = (Totp $off) })).accessToken; break }
  catch { if ($off -eq 1) { throw "Login do admin falhou: $($_.Exception.Message)" } }
}
$h = @{ Authorization = "Bearer $admin" }
$tenant = (Invoke-RestMethod "$base/admin/v1/tenants?q=$($TenantCode)" -Headers $h) | Where-Object { $_.code -eq $TenantCode } | Select-Object -First 1
if (-not $tenant) { throw "Empresa $($TenantCode) não encontrada." }
$detail = Invoke-RestMethod "$base/admin/v1/tenants/$($tenant.id)" -Headers $h
$enabled = @($detail.modules | Where-Object { $_.enabled } | ForEach-Object { $_.moduleKey })
$catalog = Invoke-RestMethod "$base/admin/v1/modules" -Headers $h

# Status: 0 = Planejado, 1 = Beta, 2 = Disponível. Ativa em ordem de dependência.
$pending = @($catalog | Where-Object { -not $_.isCore -and $_.status -in 1, 2 -and $enabled -notcontains $_.key })
$planned = @($catalog | Where-Object { $_.status -eq 0 })
for ($round = 0; $round -lt 5 -and $pending.Count -gt 0; $round++) {
  foreach ($m in @($pending)) {
    $deps = @($m.dependencies | ForEach-Object { $_.dependsOnKey })
    if (@($deps | Where-Object { $enabled -notcontains $_ }).Count -gt 0) { continue }
    $hr = @{ Authorization = "Bearer $admin"; 'X-Reauth-Totp' = (Totp) }
    Invoke-RestMethod "$base/admin/v1/tenants/$($tenant.id)/modules/$($m.key)/enable" -Method Post -Headers $hr -ContentType $json -Body (Body @{ confirmTenantCode = $tenant.code; reason = 'demonstração: todos os módulos implementados' }) | Out-Null
    $enabled += $m.key
    $pending = @($pending | Where-Object { $_.key -ne $m.key })
    Write-Output "ativado: $($m.name)"
  }
}
Write-Output "Módulos ativos: $($enabled.Count + 1) (inclui o núcleo). Planejados, ainda sem funcionalidade: $($planned.Count)."

# Identidade visual da demonstração (verde + azul, tema escuro na operação).
$draft = Invoke-RestMethod "$base/admin/v1/tenants/$($tenant.id)/branding/draft" -Headers $h
$draft.systemName = 'Demo PDV'; $draft.companyName = $TradeName; $draft.welcomeMessage = 'Bem-vindo!'
$draft.primaryColor = '#12A86B'; $draft.accentColor = '#1E8CFF'; $draft.secondaryColor = '#0F2A5C'
$draft.backgroundColor = '#EEF3F1'; $draft.surfaceColor = '#FFFFFF'; $draft.textColor = '#111827'; $draft.borderColor = '#D7DEE3'
$draft.darkBackgroundColor = '#0B1426'; $draft.darkSurfaceColor = '#111D33'; $draft.darkTextColor = '#E8ECF2'
$draft.buttonStyle = 'rounded'; $draft.fontFamily = 'Segoe UI'
Invoke-RestMethod "$base/admin/v1/tenants/$($tenant.id)/branding/draft" -Method Put -Headers $h -ContentType $json -Body (Body $draft) | Out-Null
$v = Invoke-RestMethod "$base/admin/v1/tenants/$($tenant.id)/branding/publish" -Method Post -Headers $h -ContentType $json -Body (Body @{ confirmTenantCode = $tenant.code; note = 'visual Demo PDV' })
Write-Output "Identidade visual publicada (versão $($v.version))."
