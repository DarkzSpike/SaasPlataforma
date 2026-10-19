# Operação, ambientes, segredos e continuidade

## Ambientes
| Ambiente | Banco | Chaves | Observação |
|---|---|---|---|
| Development | PostgreSQL local `comercial_dev` com RLS (`dev/run-api.ps1`); SQLite com `-Sqlite` | efêmeras (geradas em memória, aviso no log) | nunca exposto à internet |
| Testing (testes automatizados) | SQLite temporário por classe de teste | fixas de teste | — |
| Homologação | PostgreSQL + RLS (`deploy/postgres`) | cofre de segredos, distintas da produção | dados fictícios; homologação SEFAZ |
| Produção | PostgreSQL gerenciado, réplica, PITR | cofre de segredos, rotação | só após checklist abaixo |

## Segredos (nunca no código)
| Chave | Uso | Rotação |
|---|---|---|
| `Jwt:TenantSigningKey` / `Jwt:AdminSigningKey` (≥256 bits, base64) | assinatura de tokens de acesso (10/15 min) | trocar invalida sessões ativas; planejar janela |
| `License:PrivateKeyPem` (ECDSA P-256) | assina concessões de licença offline | a chave pública vai embarcada no instalador do PDV (`LicensePublicKeyPem`); rotação exige atualização do PDV — manter as duas chaves durante a transição (pendência: suporte a múltiplos `KeyId` no cliente) |
| `DataProtection:KeysPath` | protege segredos TOTP dos administradores | diretório persistente e com backup; sem ele os admins perdem o MFA |
| `Payments:<provedor>:WebhookSecret` | HMAC dos webhooks | conforme o provedor |
| `ConnectionStrings:Main` | usuário `app_comercial` (não dono das tabelas) | periódica |
| `Bootstrap:AdminEmail/AdminPassword/AdminTotpSecret` | cria o 1º administrador quando não há nenhum | remover após o primeiro deploy |

## Banco de dados
1. `deploy/postgres/001_inicial.sql` (gerado por `dotnet ef migrations script --idempotent`), aplicado na etapa de deploy com um usuário dono do schema. A API **não** migra ao iniciar (`Database:ApplyMigrationsOnStartup=false` por padrão) e se recusa a subir se houver migração pendente.
2. `deploy/postgres/002_seguranca_rls.sql` — cria `app_comercial`, RLS forçado em todas as tabelas com `TenantId`, trigger que bloqueia UPDATE/DELETE em `audit_logs` e revoga UPDATE/DELETE de tabelas de movimentação.
3. Novas migrações: `dotnet ef migrations add <Nome> --project src/Comercial.Infrastructure --startup-project src/Comercial.Infrastructure`.

## Backups e recuperação
- Objetivos sugeridos (validar com o negócio): **RPO ≤ 15 min** (PITR/WAL do PostgreSQL gerenciado) e **RTO ≤ 4 h**.
- Diário lógico: `deploy/postgres/backup.ps1` (dump + verificação + SHA-256 + retenção 35 dias). Cópia externa imutável em outra região.
- Teste de restauração mensal: `deploy/postgres/restore-test.ps1` + `GET /admin/v1/audit/verify` no banco restaurado.
- Terminais: o SQLite local nunca é apagado por inadimplência; operações pendentes ficam na fila até sincronizar.
- **Situação atual:** validado em PostgreSQL 18 local com `dev/pg-e2e.ps1` (deploy via SQL → API como `app_comercial` → dados → `backup.ps1` → `restore-test.ps1`) e pelo teste `PgDump_e_pgRestore_...`. Base pequena: os tempos medidos (backup 0,4 s; restauração 2–8 s) **não** representam o RTO de produção.

## Testes contra PostgreSQL
```powershell
$env:COMERCIAL_TEST_PG = "Host=localhost;Port=5432;Username=postgres;Password=<senha de dev>"
$env:COMERCIAL_TEST_PG_BIN = "C:\Program Files\PostgreSQL\18\bin"   # para o teste com pg_dump/pg_restore
dotnet test tests/Comercial.Tests
```
Cada classe de teste cria um banco `ct_*` descartável, aplica a migração e o RLS como superusuário e roda a API como
`app_comercial`; os bancos são apagados ao final. Sem a variável, a suíte roda em SQLite.
Atalho que já define as duas variáveis: `powershell -File dev/test-pg.ps1`.
Para o caminho completo de deploy/backup/restauração: `powershell -File dev/pg-e2e.ps1`.
A senha local do superusuário está em `dev/dev-secrets.local.json` (ignorado pelo git).

## Push para o celular do dono (Firebase Cloud Messaging)
Usado no aviso de **quebra de caixa** (fechamento com falta de dinheiro). Sem configuração, o aviso é gravado
e aparece na aba "Avisos" do app (`pushStatus = no_provider`); nada é perdido.
1. Crie um projeto no Firebase e adicione os apps Android e iOS (pacote do app Flutter).
2. Android: baixe `google-services.json` para `mobile/android/app/`. iOS: `GoogleService-Info.plist` + cadastre a **chave APNs** no Firebase.
3. Gere uma conta de serviço do Firebase (JSON) e configure na API: `Push:Fcm:ProjectId` e `Push:Fcm:CredentialsPath` (arquivo fora do repositório).
4. O app registra o aparelho em `POST /api/v1/push/tokens` após o login. Só usuários com papel **owner** recebem o aviso.
Status: integração escrita e testada com um provedor falso; **não testada contra o Firebase real**.

## Fotos de produtos (local ou Google Drive)
`ProductImages:Provider = Local` (padrão, pasta `Storage:Root/products/<empresa>`) ou `GoogleDrive`.
Google Drive: uma pasta por empresa ("código (id)") dentro de `ProductImages:GoogleDrive:RootFolderId`.
Autenticação por conta de serviço (`ProductImages:GoogleDrive:CredentialsPath`). Contas de serviço não têm cota
no "Meu Drive": use uma pasta dentro de um **Drive compartilhado** do Google Workspace (adicione a conta de serviço
como membro) ou delegação de domínio (`ProductImages:GoogleDrive:ImpersonateUser`).
As fotos **não** ficam públicas: o PDV e o app leem pela API, que confere a empresa. Trocar de provedor não
quebra as fotos antigas (a chave guarda o provedor). Status: **não testado contra o Google Drive real**.

## Faturas e Mercado Pago (mensalidade do sistema)
- **Geração automática:** o `BillingJob` roda a cada hora (`Billing:IntervalMinutes`) e cria a fatura do próximo
  período quando faltam `Billing:LeadDays` (padrão 10) dias para o fim do período. Índice único por
  empresa+assinatura+período: nunca duplica cobrança. O dono é avisado ("Nova fatura") e recebe 1 lembrete 3 dias antes.
- **Painel administrativo:** menu *Faturas* (vencem em 3/7/30 dias e vencidas, "Gerar faturas agora", baixa manual com
  MFA) e menu *Pagamentos* (Access Token, Public Key, assinatura secreta do webhook e endereço público da API —
  segredos criptografados, nunca exibidos).
- **Cliente (PDV → Faturas):** PIX com QR Code e copia-e-cola na própria tela; cartão e boleto pela página segura do
  Mercado Pago (Checkout Pro). Funciona mesmo com licença restrita (é como o cliente regulariza).
- **Confirmação:** só pelo Mercado Pago. Webhook `POST /webhooks/mercadopago` valida `x-signature` (HMAC-SHA256 de
  `id:{data.id};request-id:{x-request-id};ts:{ts};`, janela de 10 min) e **consulta o pagamento na API do Mercado Pago**
  antes de baixar (confere referência, valor e moeda). Botão "Já paguei" faz a mesma consulta quando o aviso não chega
  (ex.: servidor sem endereço público). Idempotente por pagamento+status.
- Status: integração escrita e testada com Mercado Pago simulado; **não testada contra a conta real** (exige credenciais).

## Suporte rápido (painel → empresa → "Acessar loja")
Sessão com prazo e motivo; o dono é avisado em Avisos/push. Sem consentimento, só consulta (produtos, estoque) e o
técnico pode pedir liberação. Se o dono marcou em *PDV → Suporte* o que o suporte pode alterar sem pedir (produtos,
estoque, usuários, terminais), a sessão aberta **com MFA** já vem com essas permissões. Vendas, caixa e fiscal nunca.

### Por empresa, pelo painel administrativo (aba "Armazenamento")
Cada empresa pode ter a **própria pasta e a própria credencial** (JSON da conta de serviço do Google). Tem prioridade
sobre a configuração global acima; sem configuração, a empresa usa o padrão do servidor.
- A credencial é validada (só `service_account`), criptografada com Data Protection (`DataProtection:KeysPath`
  precisa ser persistente e ter backup — sem as chaves, as credenciais salvas deixam de abrir) e nunca é devolvida
  pela API; o painel mostra apenas o e-mail da conta de serviço.
- Alterar exige MFA + digitar o código da empresa; fica na auditoria (`tenant.storage_updated`) sem o segredo.
- "Testar conexão" confere se a pasta existe e se a conta de serviço pode gravar nela.
- Arquivos gravados assim usam a chave `gdrive-t:`; trocar a credencial por outra conta sem acesso à pasta antiga
  impede a leitura das fotos antigas.

## Cadastro com teste grátis e verificação em duas etapas
- Painel → Planos → "Cadastro com teste grátis": liga/desliga, plano e dias (padrão: profissional, 14 dias). Exige o e-mail do sistema configurado.
- O lojista cria a conta no caixa ("Criar conta grátis"); a empresa só é criada depois do código de 6 dígitos enviado por e-mail.
- 2FA dos usuários: cada um ativa em "Minha conta". Perdeu o celular: dono/gerente usa "Remover 2 etapas" em Funcionários (fica na auditoria).

## Banco local do caixa
- `%LOCALAPPDATA%\ComercialPDV\local.db` é criptografado (SQLCipher). A chave fica em `local.key`, protegida pelo Windows (DPAPI) para o usuário atual.
- Copiar só o `local.db` para outro computador não permite ler as vendas. Trocar de usuário do Windows exige entrar de novo (o caixa avisa).
- Bancos antigos sem criptografia são convertidos sozinhos na primeira abertura, sem perder a fila de vendas.

## Atualização dos caixas e deploy
- Publicar versão: `dev/publish-pdv.ps1 -Version x.y.z -ApiUrl https://...` → subir o instalador em HTTPS → painel → Atualizações.
- Produção (servidor, HTTPS, backup): [DEPLOY.md](DEPLOY.md).

## Checklist antes de produção (bloqueante)
- [x] Suíte de testes rodando contra PostgreSQL com RLS ativo (local, 19/10/2026: 176 aprovados, 1 ignorado; job `postgres-integration` do CI configurado, ainda não executado no GitHub).
- [ ] Restauração de backup PostgreSQL cronometrada **no ambiente de homologação, com volume realista** (RTO real). Localmente já foi validada.
- [ ] Provedor fiscal homologado integrado e testado na SEFAZ de homologação de cada UF atendida.
- [ ] Provedor de pagamento real com adaptador de assinatura próprio e testes de webhook.
- [ ] HTTPS com certificado válido, HSTS, WAF/rate limit na borda, logs centralizados e alertas.
- [ ] Pentest externo e revisão de LGPD (DPO, base legal, política de retenção por tipo de dado).
- [x] Canal de atualização do caixa com assinatura própria + SHA-256 (painel → Atualizações). Reversão = publicar a versão anterior com número maior.
- [ ] Assinatura de código (Authenticode) do instalador — normalmente exige CNPJ.
- [ ] Primeiro deploy real seguindo [DEPLOY.md](DEPLOY.md) (arquivos Docker escritos, ainda não executados).
