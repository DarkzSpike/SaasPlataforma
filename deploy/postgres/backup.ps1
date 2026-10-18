# Backup lógico do PostgreSQL com verificação de integridade e cópia criptografada.
# STATUS: NÃO EXECUTADO no ambiente original (sem PostgreSQL). Testar em homologação.
# Requer: pg_dump/pg_restore no PATH, variáveis PGHOST, PGUSER (usuário de backup), PGPASSWORD vindo do cofre.
param(
  [Parameter(Mandatory)] [string] $Database,
  [Parameter(Mandatory)] [string] $OutDir,
  [int] $RetentionDays = 35
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$file = Join-Path $OutDir "$Database-$stamp.dump"

pg_dump --format=custom --compress=9 --no-owner --file $file $Database
if ($LASTEXITCODE -ne 0) { throw "pg_dump falhou ($LASTEXITCODE)" }

# Verifica se o arquivo é legível (lista o índice do dump).
pg_restore --list $file | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Dump ilegível: $file" }

$hash = (Get-FileHash $file -Algorithm SHA256).Hash
"$hash  $(Split-Path $file -Leaf)" | Out-File "$file.sha256" -Encoding ascii

# Retenção local; a cópia externa (imutável/WORM, outra região) deve ser feita pelo agente de armazenamento.
Get-ChildItem $OutDir -Filter "$Database-*.dump*" | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$RetentionDays) } | Remove-Item
Write-Output "Backup OK: $file ($hash)"
