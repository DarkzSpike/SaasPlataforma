#!/bin/sh
# Gera o .env com senhas/chaves aleatórias e a chave de assinatura das licenças (secrets/license_key.pem).
# Rode UMA vez no servidor. Guarde uma cópia do .env e da pasta secrets/ fora do servidor (cofre de senhas).
set -e
cd "$(dirname "$0")"
if [ -f .env ]; then echo ".env já existe - não sobrescrevi."; exit 1; fi
hex() { openssl rand -hex "$1"; }
b64() { openssl rand -base64 32 | tr -d '\n'; }
# TOTP do admin em Base32 (20 bytes)
totp=$(openssl rand 20 | base32 2>/dev/null | tr -d '=\n' || python3 -c "import base64,os;print(base64.b32encode(os.urandom(20)).decode().rstrip('='))")
mkdir -p secrets
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out secrets/license_key.pem
chmod 600 secrets/license_key.pem
cp .env.example .env
sed -i "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(hex 24)|; s|^APP_DB_PASSWORD=.*|APP_DB_PASSWORD=$(hex 24)|; \
s|^JWT_TENANT_KEY=.*|JWT_TENANT_KEY=$(b64)|; s|^JWT_ADMIN_KEY=.*|JWT_ADMIN_KEY=$(b64)|; \
s|^ADMIN_PASSWORD=.*|ADMIN_PASSWORD=Adm-$(hex 10)|; s|^ADMIN_TOTP_SECRET=.*|ADMIN_TOTP_SECRET=$totp|; s|^WEBHOOK_SECRET=.*|WEBHOOK_SECRET=$(hex 32)|" .env
chmod 600 .env
echo "Pronto. Edite API_DOMAIN, ADMIN_DOMAIN e ADMIN_EMAIL no .env."
echo "Cadastre o ADMIN_TOTP_SECRET no app autenticador (Google Authenticator → digitar chave)."
