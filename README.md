# Plataforma de Gestão Comercial (SaaS, multiempresa, White Label)

Fundação segura de um sistema comercial para varejo vendido por assinatura: API central multiempresa,
cliente Windows com operação offline, painel do desenvolvedor, app móvel (fonte) e identidade visual por cliente.

**Leia primeiro:** [docs/RELATORIO_FINAL.md](docs/RELATORIO_FINAL.md) — o que foi feito, o que foi testado,
o que **não** foi, e os riscos pendentes.

## Estrutura
| Pasta | Conteúdo | Situação |
|---|---|---|
| `src/Comercial.Domain` | Entidades, permissões, catálogo de 42 módulos | compilado |
| `src/Comercial.Shared` | Contratos, licença assinada (ECDSA), schema de identidade visual | compilado |
| `src/Comercial.Infrastructure` | EF Core (PostgreSQL/SQLite), isolamento por tenant, auditoria encadeada, migrações | compilado |
| `src/Comercial.Api` | API REST v1 (lojas), `/admin/v1` (painel), webhooks | compilado + testado |
| `src/Comercial.AdminWeb` | Painel do desenvolvedor (web estático, CSP restritiva) | testado manualmente no navegador |
| `src/Comercial.Desktop.Core` | SQLite local, fila offline, verificação de licença, sincronização | compilado + testado |
| `src/Comercial.Desktop` | Aplicativo Windows WPF (MVVM) | compilado; abre sem erro; fluxo de tela não automatizado |
| `mobile/` | App Flutter Android/iOS | **código-fonte não compilado** (sem Flutter SDK) |
| `tests/Comercial.Tests` | 76 testes de integração/segurança | 76/76 aprovados |
| `deploy/postgres` | SQL de migração, RLS, backup/restore | **não executados** (sem PostgreSQL) |

## Rodar localmente (desenvolvimento)
Requisitos: .NET SDK 10, Node (apenas para gerar o código TOTP de dev).

```bash
dotnet test Comercial.sln
```

API e painel (SQLite local, segredos de dev em `dev/dev-secrets.local.json`, ignorado pelo git):
```bash
powershell -File dev/run-api.ps1
```
```bash
powershell -File dev/run-admin.ps1
```
Painel em http://localhost:5180 — o código MFA de desenvolvimento é gerado por `node dev/totp.js`.

Cliente Windows: compile `src/Comercial.Desktop` e coloque um `desktop.json` ao lado do executável:
`{ "ApiBaseUrl": "http://localhost:5080" }`. No primeiro acesso, entre com um usuário proprietário/gerente
para ativar o terminal.

## Produção (resumo)
`Database:Provider=Postgres`, `ConnectionStrings:Main`, `Jwt:TenantSigningKey`, `Jwt:AdminSigningKey`,
`License:PrivateKeyPem`, `DataProtection:KeysPath`, `Payments:<provedor>:WebhookSecret`,
`Bootstrap:Admin*` (apenas no primeiro deploy) — todos via cofre de segredos/variáveis de ambiente.
Fora de Development a API **não inicia** sem as chaves. Detalhes em [docs/OPERACAO.md](docs/OPERACAO.md).
