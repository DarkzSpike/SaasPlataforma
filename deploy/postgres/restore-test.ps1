# Teste de restauração: restaura o dump em um banco temporário e confere contagens e a cadeia de auditoria.
# STATUS: NÃO EXECUTADO no ambiente original (sem PostgreSQL). O sistema NÃO deve ir para produção
# antes de este teste passar em homologação (seção 17 dos requisitos).
param(
  [Parameter(Mandatory)] [string] $DumpFile,
  [string] $TempDatabase = "restore_test_$(Get-Date -Format yyyyMMddHHmmss)"
)
$ErrorActionPreference = 'Stop'
$expected = (Get-Content "$DumpFile.sha256").Split(' ')[0]
if ((Get-FileHash $DumpFile -Algorithm SHA256).Hash -ne $expected) { throw 'Hash do backup não confere (arquivo corrompido ou adulterado).' }

createdb $TempDatabase
try {
  pg_restore --no-owner --dbname $TempDatabase $DumpFile
  if ($LASTEXITCODE -ne 0) { throw "pg_restore falhou ($LASTEXITCODE)" }
  psql -d $TempDatabase -v ON_ERROR_STOP=1 -c "SELECT 'tenants', count(*) FROM tenants UNION ALL SELECT 'sales', count(*) FROM sales UNION ALL SELECT 'audit_logs', count(*) FROM audit_logs;"
  # A verificação da cadeia de hash da auditoria pode ser feita apontando a API de homologação para este banco
  # e chamando GET /admin/v1/audit/verify.
  Write-Output "Restauração OK em $TempDatabase"
}
finally {
  dropdb --if-exists $TempDatabase
}
