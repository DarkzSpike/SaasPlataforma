# Auditoria de SQL Injection — 26/10/2026

## 1. Resumo executivo

O projeto **não tinha SQL Injection explorável por usuários** (lojistas, funcionários, app, painel ou integrações).
Toda a API acessa o banco pelo **Entity Framework Core** (LINQ → consultas parametrizadas pelo driver), e os únicos
pontos de SQL escrito à mão usam **texto fixo com parâmetros**. Não existe ordenação, nome de coluna ou de tabela
vindos do usuário.

Foi encontrado **1 problema médio** (fora do caminho de usuários): scripts de implantação montavam
`ALTER ROLE ... PASSWORD '<senha>'` colando a senha no texto do SQL, executado como superusuário do banco.
Foram corrigidos também **3 pontos baixos** de validação. Nada disso significa "100% seguro": ver riscos residuais (seção 8).

| Risco | Quantidade | Situação |
|---|---|---|
| Crítico | 0 | — |
| Alto | 0 | — |
| Médio | 1 | corrigido |
| Baixo | 3 | corrigidos |

## 2. Arquitetura auditada

| Componente | Tecnologia | Acesso ao banco |
|---|---|---|
| API (`src/Comercial.Api`) | .NET 10, ASP.NET Core | EF Core 10 + Npgsql (PostgreSQL 18) / SQLite (testes e desenvolvimento) |
| Caixa (`src/Comercial.Desktop*`) | WPF .NET 10 | Microsoft.Data.Sqlite + SQLCipher (banco local) |
| Painel admin (`src/Comercial.AdminWeb`) | JS estático | só chama a API (sem SQL) |
| App (`mobile/`) | Flutter | só chama a API (sem SQL) |
| Scripts (`dev/`, `deploy/`) | PowerShell, sh, Docker Compose | `psql` |

Busca feita em todo o `src/` por: `FromSqlRaw`, `ExecuteSqlRaw`, `SqlQueryRaw`, `FromSqlInterpolated`, `CommandText`,
`ExecuteNonQuery/Scalar/Reader`, `DbCommand`, `set_config`, `EF.Functions`, `EF.Property`, ordenação dinâmica,
parâmetros `sort/orderBy/take/skip`; e em `dev/`, `deploy/`, `tests/` por SQL montado com variáveis.

## 3. Achados e correções

| # | Risco | Local | Causa | Impacto | Correção |
|---|---|---|---|---|---|
| M1 | **Médio** | `dev/run-api.ps1:36`, `dev/pg-e2e.ps1:24`, `deploy/docker/docker-compose.yml:35` | Senha do usuário do banco colada no texto de `ALTER ROLE ... PASSWORD '...'` | A senha vem do operador (arquivo de segredos/`.env`), não de usuários. Mas uma senha com `'` quebraria o comando ou executaria outro SQL **como superusuário** | Senha passada como **variável do psql** (`-v pw=...` + `:'pw'`, citada pelo próprio psql). Testado com `x'; DROP TABLE tenants; --`: chegou como texto e nada foi executado |
| B1 | Baixo | `GET /api/v1/products?skip=` | `skip` negativo ia direto para `Skip()` | Erro 500 (sem vazamento, mas é falha evitável) | `InputRules.Skip`: 0 a 1.000.000, senão `400 invalid_paging` |
| B2 | Baixo | Buscas `?q=` de produtos, clientes, fornecedores e empresas (admin) | Sem limite de tamanho | Buscas longas e caras (abuso) | `InputRules.SearchTerm`: até 100 caracteres, senão `400 invalid_query`. Apóstrofos, acentos e Unicode **são aceitos** |
| B3 | Baixo | `deploy/postgres/002_seguranca_rls.sql` | Comentário dizia "NÃO EXECUTADO" | Documentação enganosa | Atualizado (o script roda em toda a suíte PostgreSQL) |

### Pontos de SQL manual revisados (seguros, mantidos)

| Arquivo | SQL | Por que é seguro |
|---|---|---|
| `Infrastructure/Data/RlsConnectionInterceptor.cs` | `set_config('app.tenant_id', @t, false)...` | valores por parâmetro (`@t`, `@p`) |
| `Infrastructure/Data/AppDbContext.cs` | `SELECT pg_advisory_xact_lock(727274)` | constante, sem entrada externa |
| `Desktop.Core/LocalDatabase.cs` | `INSERT/SELECT/UPDATE` do banco local | textos fixos com `$parâmetros`; `LIKE` com escape de `%`/`_`; chave do SQLCipher via `SqliteConnectionStringBuilder`/parâmetro |
| `tests/Comercial.Tests/PostgresTestDb.cs` | `CREATE/DROP DATABASE`, `ALTER ROLE` | só testes; nomes gerados de `Guid` e senha constante |

## 4. Defesa em profundidade já existente (verificada)

- **Menor privilégio no PostgreSQL** (consultado no banco real): a API conecta como `app_comercial` — sem superusuário,
  sem `CREATEROLE`/`CREATEDB`, **sem `BYPASSRLS`**, sem `CREATE` no banco/schema (sem DDL), **não é dona das tabelas**
  (dono: `postgres`); `DELETE` só em 6 tabelas de vínculo; `UPDATE/DELETE/TRUNCATE` revogados em `audit_logs` e nos
  lançamentos financeiros/estoque.
- **Isolamento por empresa no banco**: RLS **forçado** nas 56 tabelas por empresa; sem empresa definida a consulta não vê linhas
  (falha fechada). Mesmo uma injeção hipotética não atravessaria empresas.
- **Senhas**: Argon2id (64 MB, 3 iterações), comparação em tempo constante. Tokens de sessão e códigos guardados só como hash.
- **Erros**: o usuário recebe só `{status, code: "internal_error", title: "Erro interno.", traceId}`; o detalhe vai para o log.
  `EnableSensitiveDataLogging` **não** está ligado (valores das consultas não vão para o log).
- **Segredos**: fora do código (`dev-secrets.local.json` e `.env` no `.gitignore`; produção por variáveis de ambiente/arquivos de segredo).

## 5. Arquivos alterados

- `dev/run-api.ps1`, `dev/pg-e2e.ps1`, `deploy/docker/docker-compose.yml` — senha por variável do psql (M1)
- `src/Comercial.Api/Security/InputRules.cs` (novo) — validação de busca e paginação
- `src/Comercial.Api/Endpoints/TenantEndpoints.cs`, `BusinessEndpoints.cs`, `AdminEndpoints.cs` — uso do `InputRules` (B1, B2)
- `deploy/postgres/002_seguranca_rls.sql` — comentário (B3)
- `tests/Comercial.Tests/SqlInjectionTests.cs` (novo) — testes abaixo
- Nenhuma mudança de esquema do banco.

## 6. Testes executados (resultados reais, 26/10/2026)

`SqlInjectionTests.cs` — 44 casos, cada um com 12 cargas de ataque (`' OR '1'='1`, `' OR 1=1 --`, `"; DELETE FROM users; --`,
`UNION SELECT "PasswordHash"`, `%`, `_`, `pg_sleep`, comentários etc.):

- **Login**: carga em cada campo (empresa, e-mail, senha) → sempre `401 invalid_credentials`, nunca 200 nem 500; o login legítimo continua funcionando.
- **Gravação e leitura literal**: produto `Copo d'água "Premium" ; DROP TABLE products; -- 😀 ção` e cliente `Ana D'Ávila` gravados e lidos exatamente iguais; busca por `d'água` encontra.
- **Buscas** (produtos, clientes, fornecedores): carga não traz dados a mais (`%` e `OR 1=1` não viram "traz tudo") nem de outra empresa.
- **Paginação/tipos**: `skip=-5` → 400; busca de 101 caracteres → 400; `skip=abc` → 400; id `1 OR 1=1` → 404.
- **Painel admin**: busca de empresas com carga → lista vazia.
- **Escrita**: motivo de entrada de estoque com `UPDATE ... SET Quantity=99999` não altera outros saldos.
- **Transação**: venda (venda + itens + pagamento + estoque) com chave maliciosa grava uma vez, repetição não duplica, saldo correto.
- **Erro do banco**: mensagem com SQL, tabela e senha não aparece na resposta (só `internal_error` + `traceId`).
- **Banco local do caixa** (SQLCipher): busca, chave/valor e fila com as 12 cargas tratadas como dado.
- **Trava contra regressão** (`SqlSourceGuardTests`): falha se alguém escrever `FromSqlRaw/ExecuteSqlRaw/SqlQueryRaw` com
  interpolação/concatenação, `CommandText = $"..."`/`+ variável`, `string.Format/Concat` em SQL, ou SQL manual fora dos
  arquivos revisados; e se scripts voltarem a colar senha em SQL. **Provado**: um arquivo de sabotagem com
  `CommandText = $"SELECT ... '{nome}'"` foi detectado (arquivo e linha) e removido.

Suítes completas: **SQLite 245 aprovados / 4 ignorados; PostgreSQL 18 com RLS 248 aprovados / 1 ignorado; 0 falhas.**
Pacotes vulneráveis (NuGet): nenhum. Script corrigido validado na prática: API reiniciada pelo `run-api.ps1` e login OK no PostgreSQL com `app_comercial`.

## 7. O que ainda exige atenção

- A CI (`.github/workflows/ci.yml`) roda tudo isso, mas só em push para `main` e em pull requests — o branch de trabalho atual não dispara a CI sozinho.
- Os testes da CI no Linux (job PostgreSQL) incluem o banco local do caixa com SQLCipher; não foi verificado num runner Linux nesta auditoria.

## 8. Riscos residuais e limitações

- **`psql -v pw=...`** deixa a senha visível na lista de processos durante a execução do script (máquina do operador/contêiner
  de migração). Não é injeção; em produção, prefira gerar o `ALTER ROLE` dentro do contêiner a partir de um arquivo de segredo.
- A tradução de `Contains()` para `LIKE`/`strpos` e o escape de `%`/`_` dependem do provedor do EF (Npgsql/SQLite) — **coberto por testes nos dois**.
- Fora do escopo de SQL: APIs externas (Mercado Pago, Focus NFe, Google) usam HTTP/JSON — validadas em outras rodadas.
- A auditoria é estática + testes; **não** houve pentest externo nem análise com ferramenta SAST comercial.
- Senha do superusuário do PostgreSQL de desenvolvimento fica em `dev/dev-secrets.local.json` (fora do Git); proteja o arquivo.

## 9. Recomendações para produção

1. Nunca dar à API outro usuário do banco que não o `app_comercial` (nada de `postgres`/dono das tabelas).
2. Migrações rodam com um usuário dono separado, só no deploy (o compose já faz assim).
3. Senhas e chaves só por variáveis de ambiente/arquivos de segredo; trocar todas ao ir para produção.
4. Habilitar a CI também para o branch de trabalho, ou trabalhar com pull requests.
5. Logs com retenção definida e acesso restrito (o `traceId` liga a resposta ao log).

## 10. Regras para não reintroduzir SQL Injection

1. Use **LINQ do EF Core** para tudo. Precisa de SQL manual? Use `FromSql($"... {valor}")`/`ExecuteSql($"...")`
   (interpolação **parametrizada** do EF) ou `DbCommand` com `Parameters` — e inclua o arquivo na lista revisada do
   `SqlSourceGuardTests` só depois de revisão.
2. **Nunca** `FromSqlRaw/ExecuteSqlRaw` com `$"..."` ou `+`.
3. Nome de coluna/tabela/ordenação vindo do usuário: **somente** por lista de valores permitidos no servidor (`switch` para expressões LINQ fixas).
4. Buscas e paginação: sempre `InputRules.SearchTerm` / `InputRules.Skip`; `Take` com limite fixo.
5. Scripts: segredo em SQL só via `psql -v nome=valor` + `:'nome'`.
6. Os testes `SqlInjectionTests`, `LocalDatabaseInjectionTests` e `SqlSourceGuardTests` precisam continuar passando.
