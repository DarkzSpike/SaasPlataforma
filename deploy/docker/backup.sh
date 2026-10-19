#!/bin/sh
# Backup diário (agende no cron: 0 3 * * * /caminho/deploy/docker/backup.sh).
# Copia o banco E as chaves do DataProtection: sem as chaves, tokens do Mercado Pago/SMTP guardados ficam ilegíveis.
set -e
cd "$(dirname "$0")"
d=backups/$(date +%Y%m%d-%H%M)
mkdir -p "$d"
docker compose exec -T db pg_dump -U postgres -Fc comercial > "$d/comercial.dump"
docker compose exec -T api tar -C /data -cz keys storage > "$d/api-data.tgz"
find backups -mindepth 1 -maxdepth 1 -type d -mtime +14 -exec rm -rf {} +
echo "Backup em $d. Copie também para fora do servidor (ex.: rclone para o Google Drive)."
