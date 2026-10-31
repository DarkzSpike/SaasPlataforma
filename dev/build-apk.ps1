# Gera o APK do app Android e (opcional) já publica a atualização no servidor de desenvolvimento.
#
# Uso mais comum (gera versão nova + publica: os celulares mostram "Nova versão" e atualizam com um toque):
#   powershell -ExecutionPolicy Bypass -File dev\build-apk.ps1 -Publish -Notes "O que mudou"
#
# Parâmetros:
#   -ApiUrl   endereço padrão gravado no app (padrão: o túnel cloudflared aberto neste computador; sem túnel,
#             o IP deste computador na rede local, porta 5080).
#             Para produção: -ApiUrl https://api.seudominio.com.br
#   -NoBump   não sobe o número da versão (use só para gerar de novo a mesma versão).
#   -Publish  copia o APK para a pasta de downloads da API e publica a versão (painel → Atualizações).
#
# A chave de assinatura (mobile/android/upload-keystore.jks + key.properties) é criada no 1º uso e fica FORA do Git.
# GUARDE UMA CÓPIA: sem ela o Android não aceita instalar versões novas por cima (teria de desinstalar o app).
param([string]$ApiUrl, [switch]$NoBump, [switch]$Publish, [string]$Notes = "")
# 'Continue': keytool/flutter/gradle escrevem avisos no stderr; o resultado é conferido por $LASTEXITCODE.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$mobile = Join-Path $root 'mobile'
$env:JAVA_HOME = 'E:\dev-tools\jdk17'
$env:Path = "E:\dev-tools\flutter\bin;E:\dev-tools\jdk17\bin;$env:Path"
$env:FLUTTER_SUPPRESS_ANALYTICS = 'true'

# Túnel do Cloudflare aberto neste computador (cloudflared): o app já sai conectado pela internet.
if (-not $ApiUrl) {
  foreach ($p in 20241..20245) {
    try {
      $t = Invoke-RestMethod "http://127.0.0.1:$p/quicktunnel" -TimeoutSec 2
      if ($t.hostname) { $ApiUrl = "https://$($t.hostname)"; Write-Host "Usando o túnel da internet: $ApiUrl"; break }
    } catch { }
  }
}
# Sem túnel: IP do computador na rede local (o celular precisa estar no mesmo Wi-Fi/roteador).
if (-not $ApiUrl) {
  $ip = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))' -and $_.PrefixOrigin -ne 'WellKnown' } |
    Sort-Object InterfaceMetric | Select-Object -First 1 -ExpandProperty IPAddress
  if (-not $ip) { throw "Não achei o IP da rede local. Informe -ApiUrl http://SEU-IP:5080" }
  $ApiUrl = "http://${ip}:5080"
}

# 1. Chave de assinatura fixa (criada uma vez).
$keyProps = Join-Path $mobile 'android/key.properties'
$jks = Join-Path $mobile 'android/upload-keystore.jks'
if (-not (Test-Path $keyProps)) {
  $pass = -join ((48..57) + (65..90) + (97..122) | Get-Random -Count 24 | ForEach-Object { [char]$_ })
  & keytool -genkeypair -v -keystore $jks -storetype JKS -keyalg RSA -keysize 2048 -validity 10000 -alias comercial `
    -storepass $pass -keypass $pass -dname "CN=Sistema Comercial, O=Sistema Comercial, C=BR" 2>&1 | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Falha ao criar a chave de assinatura" }
  [IO.File]::WriteAllText($keyProps, "storePassword=$pass`nkeyPassword=$pass`nkeyAlias=comercial`nstoreFile=$($jks.Replace('\','/'))`n", (New-Object Text.UTF8Encoding $false))
  Write-Host "Chave de assinatura criada: $jks  (FAÇA BACKUP deste arquivo e do key.properties)" -ForegroundColor Yellow
}

# 2. Versão: 1.0.3+3 -> 1.0.4+4
$pubspec = Join-Path $mobile 'pubspec.yaml'
$text = [IO.File]::ReadAllText($pubspec)
$originalPubspec = $text   # volta ao número anterior se a compilação falhar
$m = [regex]::Match($text, '(?m)^version:\s*(\d+)\.(\d+)\.(\d+)\+(\d+)')
if (-not $m.Success) { throw "Linha 'version: x.y.z+n' não encontrada no pubspec.yaml" }
$major, $minor, $patch, $code = 1..4 | ForEach-Object { [int]$m.Groups[$_].Value }
if (-not $NoBump -and (Test-Path (Join-Path $mobile 'build/app/outputs/flutter-apk/app-release.apk'))) {
  $patch++; $code++
  $text = $text.Remove($m.Index, $m.Length).Insert($m.Index, "version: $major.$minor.$patch+$code")
  [IO.File]::WriteAllText($pubspec, $text, (New-Object Text.UTF8Encoding $false))
}
$versionName = "$major.$minor.$patch"
Write-Host "Gerando versão $versionName (código $code) com servidor padrão $ApiUrl ..."

# 3. Build
Push-Location $mobile
try {
  flutter build apk --release "--dart-define=API_BASE_URL=$ApiUrl"
  if ($LASTEXITCODE -ne 0) {
    [IO.File]::WriteAllText($pubspec, $originalPubspec, (New-Object Text.UTF8Encoding $false))
    throw "Falha no flutter build apk (a versão não foi alterada)"
  }
} finally { Pop-Location }

$apk = Join-Path $mobile 'build/app/outputs/flutter-apk/app-release.apk'
$name = "comercial-$versionName.apk"
New-Item -ItemType Directory -Force (Join-Path $root 'artifacts') | Out-Null
Copy-Item $apk (Join-Path $root "artifacts/$name") -Force
$sha = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "`nAPK: $(Join-Path $root "artifacts/$name")  ($([math]::Round((Get-Item $apk).Length/1MB,1)) MB)"
Write-Host "SHA-256: $sha"

# 4. Publicar no servidor de desenvolvimento (download pelo próprio servidor + aviso no app)
if ($Publish) {
  $downloads = Join-Path $root 'src/Comercial.Api/dev-data/storage/downloads'   # mesma pasta que a API usa (Storage:Root/downloads)
  New-Item -ItemType Directory -Force $downloads | Out-Null
  Copy-Item $apk (Join-Path $downloads $name) -Force
  # O servidor lê este arquivo: os celulares com versão menor perguntam "Atualizar agora?" ao abrir o app
  # (não depende do login de administrador). O endereço é relativo: vale para a rede local e para a internet.
  $manifest = [ordered]@{ versionCode = $code; versionName = $versionName; file = $name; notes = $Notes; mandatory = $false } | ConvertTo-Json
  [IO.File]::WriteAllText((Join-Path $downloads 'mobile-latest.json'), $manifest, (New-Object Text.UTF8Encoding $false))
  Write-Host "Versão $versionName disponível: os celulares vão perguntar 'Atualizar agora?' ao abrir o app." -ForegroundColor Green
  $s = Get-Content (Join-Path $PSScriptRoot 'dev-secrets.local.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  $totp = node (Join-Path $PSScriptRoot 'totp.js')
  # Sem isto o script dizia "Publicado" mesmo quando o login do administrador era recusado.
  $ErrorActionPreference = 'Stop'
  try {
  $login = Invoke-RestMethod -Method Post "http://localhost:5080/admin/v1/auth/login" -ContentType 'application/json' `
    -Body (@{ email = $s.AdminEmail; password = $s.AdminPassword; totpCode = $totp } | ConvertTo-Json)
  $reauth = node (Join-Path $PSScriptRoot 'totp.js') 1   # código seguinte (o mesmo código não vale duas vezes)
  $body = @{ versionCode = $code; versionName = $versionName; url = "$ApiUrl/downloads/$name"; notes = $Notes; mandatory = $false } | ConvertTo-Json
  $bytes = [Text.Encoding]::UTF8.GetBytes($body)
  Invoke-RestMethod -Method Put "http://localhost:5080/admin/v1/settings/mobile-release" -ContentType 'application/json; charset=utf-8' `
    -Headers @{ Authorization = "Bearer $($login.accessToken)"; 'X-Reauth-Totp' = $reauth } -Body $bytes | Out-Null
  Write-Host "Publicado: os celulares com versão menor que $versionName vão mostrar 'Nova versão' ao abrir o app." -ForegroundColor Green
  Write-Host "Download direto: $ApiUrl/downloads/$name"
  } catch {
    Write-Host "(Registro no painel admin não feito: $($_.Exception.Message). O aviso no app funciona mesmo assim.)" -ForegroundColor DarkGray
    Write-Host "Download direto: $ApiUrl/downloads/$name"
  }
}
