# Colocar em produção (servidor na internet)

Este guia leva o sistema do seu computador para um servidor de verdade, com HTTPS, backup e atualização dos caixas.
Funciona operando **no CPF**: nada aqui exige CNPJ. A NFS-e da mensalidade e a NFC-e das lojas ficam desligadas até existir CNPJ.

> **Situação honesta:** os arquivos de `deploy/docker` foram escritos e revisados, mas **não foram executados num servidor real**
> (este computador não tem Docker). Faça o primeiro deploy com calma, seguindo o checklist do fim.

## 1. O que você precisa

| Item | Sugestão | Custo aproximado |
|---|---|---|
| Servidor Linux (VPS) com 2 GB de RAM | Hostinger, Contabo, DigitalOcean, Magalu Cloud (Ubuntu 24.04) | R$ 30–60/mês |
| Domínio | registro.br (`.com.br` pode ser registrado no CPF) | ~R$ 40/ano |
| E-mail para envio | Gmail com "senha de app" ou Brevo (300/dia grátis) | grátis |
| Conta Mercado Pago (pessoa física) | para receber as mensalidades | taxa por pagamento |

## 2. DNS

No painel do domínio crie dois registros **A** apontando para o IP do servidor:

- `api.seudominio.com.br` → API (os caixas e o app conectam aqui)
- `admin.seudominio.com.br` → painel administrativo

## 3. Instalar e subir

No servidor (via SSH):

```bash
curl -fsSL https://get.docker.com | sh
git clone https://github.com/DarkzSpike/SaasPlataforma.git comercial
cd comercial/deploy/docker
sh gerar-segredos.sh          # cria .env e secrets/license_key.pem com chaves aleatórias
nano .env                     # ajuste API_DOMAIN, ADMIN_DOMAIN e ADMIN_EMAIL
docker compose up -d --build  # banco + scripts do banco + API + painel + HTTPS automático
docker compose logs -f api    # confira se subiu sem erro
```

O que acontece:
- **db**: PostgreSQL sem porta exposta na internet.
- **migrate**: aplica `deploy/postgres/001_inicial.sql` e `002_seguranca_rls.sql` (idempotentes) a cada subida; a API conecta como `app_comercial`, que **não** é dona das tabelas, então o isolamento por empresa (RLS) vale de verdade.
- **api / admin**: rodam como usuário sem privilégios; confiam nos cabeçalhos do proxy só vindos da rede interna do Docker.
- **caddy**: emite e renova o certificado HTTPS sozinho.

Primeiro acesso: abra `https://admin.seudominio.com.br`, entre com `ADMIN_EMAIL` + `ADMIN_PASSWORD` do `.env` e o código do autenticador (cadastre o `ADMIN_TOTP_SECRET` no Google Authenticator → "Inserir chave de configuração"). **Troque a senha** depois do primeiro login.

## 4. Configurar pelo painel

1. **E-mail**: servidor SMTP (necessário para "esqueci minha senha" e para o cadastro com teste grátis).
2. **Pagamentos**: Access Token do Mercado Pago + URL pública `https://api.seudominio.com.br` (os avisos de pagamento chegam em `/webhooks/mercadopago`).
3. **Planos**: confira preços; em "Cadastro com teste grátis" escolha plano e dias.
4. **NFS-e**: deixe desligada enquanto operar no CPF.

## 5. Caixas (PDV) das lojas

1. Pegue a chave **pública** das licenças no servidor: `openssl ec -in secrets/license_key.pem -pubout > license_pub.pem` e copie para o seu computador.
2. No seu computador:
   `powershell -ExecutionPolicy Bypass -File dev\publish-pdv.ps1 -Version 1.0.0 -ApiUrl https://api.seudominio.com.br -LicensePublicKeyFile license_pub.pem`
   Gera `artifacts\pdv` com o `desktop.json` (endereço da API + chave fixada) e, se o [Inno Setup 6](https://jrsoftware.org/isinfo.php)
   estiver instalado, o instalador `artifacts\Comercial-1.0.0-setup.exe` e o SHA-256.
3. Suba o instalador no GitHub → Releases (endereço HTTPS público).
4. Painel → **Atualizações**: versão, endereço e SHA-256. Os caixas avisam no próximo login e instalam com um clique.
   A informação é assinada com a chave das licenças e o caixa confere o SHA-256 do arquivo: um instalador trocado é recusado.

> Sem certificado de assinatura de código (Authenticode, ~R$ 400+/ano e normalmente exige CNPJ), o Windows mostra
> "Editor desconhecido / SmartScreen" na primeira instalação. Clique em "Mais informações → Executar assim mesmo".
> As atualizações automáticas continuam protegidas pela assinatura própria descrita acima.

## 6. Backup (não pule)

```bash
crontab -e
# todo dia às 3h:
0 3 * * * /root/comercial/deploy/docker/backup.sh >> /var/log/comercial-backup.log 2>&1
```

O backup copia o banco **e** as chaves do DataProtection (`/data/keys`). Sem essas chaves, os tokens do Mercado Pago,
a senha do SMTP e os segredos de 2FA guardados no banco ficam ilegíveis. Copie a pasta `backups/` para fora do servidor
(ex.: `rclone` para o Google Drive) e guarde o `.env` + `secrets/` num cofre de senhas.

Restaurar: `docker compose exec -T db pg_restore -U postgres -d comercial --clean < backups/AAAAMMDD-HHMM/comercial.dump`
e extrair `api-data.tgz` dentro do volume `/data` da API.

## 7. Atualizar o servidor

```bash
cd ~/comercial && git pull && cd deploy/docker && docker compose up -d --build
```

Antes de cada release: regenerar `deploy/postgres/001_inicial.sql` (`dotnet ef migrations script --idempotent`) e rodar
`dev/test-pg.ps1` (testes contra PostgreSQL com RLS).

## Checklist do primeiro deploy

- [ ] `https://api.seudominio.com.br/health` responde `{"status":"ok"}`
- [ ] Login no painel com 2FA funcionando; senha inicial trocada
- [ ] E-mail de teste enviado pelo painel
- [ ] Criar empresa de teste, entrar pelo caixa, vender, sincronizar
- [ ] Pagamento de teste no Mercado Pago (credenciais TEST-) muda a fatura para paga
- [ ] `backup.sh` executado manualmente e arquivo copiado para fora do servidor
- [ ] Restauração testada num banco separado ao menos uma vez
