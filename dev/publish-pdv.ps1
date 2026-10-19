# Publica o caixa (PDV) para distribuição:
#  1. dotnet publish (Release, win-x64, com o .NET embutido: o computador da loja não precisa instalar nada);
#  2. gera o instalador com Inno Setup, se estiver instalado (ISCC.exe);
#  3. mostra o SHA-256 para colar no painel admin → Atualizações.
# Uso: powershell -ExecutionPolicy Bypass -File dev\publish-pdv.ps1 -Version 1.0.1 -ApiUrl https://api.seudominio.com.br -LicensePublicKeyFile license_pub.pem
#  -ApiUrl: endereço da API gravado no desktop.json do instalador.
#  -LicensePublicKeyFile: chave PÚBLICA das licenças (openssl ec -in secrets/license_key.pem -pubout). Fixada no caixa,
#   impede que um servidor falso emita licenças ou atualizações. Sem ela o caixa aceita a chave do 1º servidor que contatar.
param([Parameter(Mandatory = $true)][string]$Version, [string]$ApiUrl, [string]$LicensePublicKeyFile)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão no formato 1.2.3" }
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts/pdv'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'src/Comercial.Desktop') -c Release -r win-x64 --self-contained true `
  -p:Version=$Version -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "Falha no dotnet publish" }

if ($ApiUrl) {
  if ($ApiUrl -notmatch '^https://') { throw "ApiUrl precisa ser https://" }
  $cfg = [ordered]@{ ApiBaseUrl = $ApiUrl.TrimEnd('/') }
  if ($LicensePublicKeyFile) { $cfg.LicensePublicKeyPem = (Get-Content $LicensePublicKeyFile -Raw).Trim() }
  [IO.File]::WriteAllText((Join-Path $out 'desktop.json'), ($cfg | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
  Write-Host "desktop.json gravado (API: $($cfg.ApiBaseUrl))"
} else {
  Write-Host "AVISO: sem -ApiUrl o caixa usa o endereço de desenvolvimento (https://localhost:5001)." -ForegroundColor Yellow
}

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  Write-Host "`nInno Setup não encontrado. Os arquivos estão em $out (dá para copiar a pasta para o caixa)."
  Write-Host "Para gerar o instalador: instale o Inno Setup 6 (jrsoftware.org) e rode este script de novo."
  exit 0
}
& $iscc "/DAppVersion=$Version" "/DSourceDir=$out" (Join-Path $root 'deploy/installer/Comercial.iss')
if ($LASTEXITCODE -ne 0) { throw "Falha no Inno Setup" }
$setup = Join-Path $root "artifacts/Comercial-$Version-setup.exe"
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "`nInstalador: $setup"
Write-Host "SHA-256:    $sha"
Write-Host "`nPróximos passos: suba o .exe num endereço HTTPS (ex.: GitHub → Releases → v$Version) e publique no painel admin → Atualizações."
