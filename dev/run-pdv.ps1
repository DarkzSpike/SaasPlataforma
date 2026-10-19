# Compila e abre o PDV Windows (Release).
# Um PDV já aberto trava as DLLs da pasta bin: ele é fechado antes de compilar.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$old = Get-Process Comercial.Desktop -ErrorAction SilentlyContinue
if ($old) {
  Write-Host "Fechando PDV que ja estava aberto (PID $($old.Id -join ', '))..."
  $old | Stop-Process -Force
  $old | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

dotnet build (Join-Path $root 'src/Comercial.Desktop/Comercial.Desktop.csproj') -c Release -v q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host 'Falha ao compilar o PDV.' -ForegroundColor Red; exit 1 }

Start-Process (Join-Path $root 'src\Comercial.Desktop\bin\Release\net10.0-windows\Comercial.Desktop.exe')
Write-Host 'PDV aberto.' -ForegroundColor Green
