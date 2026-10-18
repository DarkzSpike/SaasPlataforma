# Relatório final — fundação da plataforma SaaS de gestão comercial

Data: 18/10/2026. Ambiente: Windows 10, .NET SDK 10.0.402, Node 24, PostgreSQL 18.6 (instalado via winget na etapa 2). **Sem** Flutter ou Docker.

## 1. Resumo honesto

| | Situação |
|---|---|
| Testes automatizados — SQLite | **76 aprovados**, 4 ignorados (específicos de PostgreSQL) |
| Testes automatizados — PostgreSQL 18 + RLS | **79 aprovados**, 1 ignorado (específico de SQLite); 4 execuções seguidas sem falha |
| Caminho de deploy PostgreSQL | `001_inicial.sql` + `002_seguranca_rls.sql` via psql → API como `app_comercial` → dados reais → `backup.ps1` → `restore-test.ps1`: **OK** (`dev/pg-e2e.ps1`) |
| Compilação | solução inteira em Release com avisos tratados como erro: **0 avisos, 0 erros** |
| Dependências vulneráveis (NuGet) | **nenhuma** (`dotnet list package --vulnerable --include-transitive`) |
| Painel web | testado manualmente no navegador: login com MFA, criação de empresa, módulos, publicação de identidade visual |
| App Windows | compila; abre a janela sem erro; as telas **não** foram exercitadas por automação (a lógica offline por trás delas é testada) |
| App Flutter | **código escrito, não compilado** (sem SDK) |
| Fiscal (NFC-e/NF-e) | **não integrado à SEFAZ**: só o ciclo de estados; o provedor padrão nunca autoriza |
| Pagamentos | webhook genérico assinado (HMAC) e idempotente; **nenhum provedor real** conectado |

Não é um sistema pronto para produção. É a fundação segura (fases 1–5, 7–9 em grande parte) com os
controles críticos implementados e verificados por testes. O checklist bloqueante está em [OPERACAO.md](OPERACAO.md).

## 2. Diagnóstico (fase 1)
A pasta estava **vazia**: não havia código, tecnologia ou dados a migrar. Projeto iniciado do zero com a stack
recomendada no pedido (C#/.NET LTS, ASP.NET Core, PostgreSQL, WPF/MVVM + SQLite, Flutter). Nenhuma tecnologia extra
foi adicionada além de: EF Core (acesso a dados), Npgsql (driver PostgreSQL), Konscious Argon2 (hash de senha), JwtBearer.

## 3. Arquitetura

```
 Windows (WPF) ──┐  HTTPS, token curto + refresh rotativo       ┌── PostgreSQL (RLS por tenant)
 Android/iOS ────┼──────────────► API ASP.NET Core /api/v1 ─────┤
 Painel web ─────┘  (token admin separado, MFA)  /admin/v1      └── Armazenamento de imagens (fora do webroot)
 Provedor de pagamento ── HMAC ──► /webhooks/payments/{p}
```
- **Camadas:** Domain (entidades/regras) → Infrastructure (EF Core, isolamento, auditoria) → Api (serviços + endpoints). Shared contém contratos e o formato de licença usados por API, PDV e testes.
- **Dois esquemas de autenticação independentes** (chaves e audiências diferentes): token de loja não abre o painel e vice-versa.
- **Autorização centralizada** em `FeatureAuthorization`: usuário + tenant + licença + módulo contratado + permissão + escopo de suporte. Cada endpoint declara `RequireFeature(módulo, permissão, write)`; não há verificações espalhadas.

## 4. Isolamento multiempresa
1. `tenant_id` **nunca** vem do cliente: é lido da claim do token (validada) ou de uma sessão de suporte válida. Nenhum contrato da API de loja aceita `tenantId` (teste prova que um `tenantId` injetado no corpo é ignorado).
2. **Filtro global do EF** em toda entidade com `TenantId`; sem tenant definido o filtro não retorna nada (falha fechada).
3. `SaveChanges` recusa gravar em outro tenant, mudar `TenantId`, alterar entidades imutáveis e excluir vendas/documentos.
4. **Chaves estrangeiras compostas** `(TenantId, Id)` nas relações críticas (venda→loja/caixa, item→produto, movimento→produto/loja): o próprio banco impede referência cruzada.
5. Busca por Id em outro tenant responde 404 idêntico a "não existe" e grava `security.cross_tenant_probe` na auditoria.
6. **RLS no PostgreSQL** (camada extra): `RlsConnectionInterceptor` + `deploy/postgres/002_seguranca_rls.sql`. **Validado em PostgreSQL 18** (`PostgresSecurityTests`): com SQL direto, sem o filtro do EF, o usuário `app_comercial` só vê o próprio tenant; sem tenant definido não vê nada; não consegue gravar em outro tenant, excluir vendas, alterar movimentos, criar/apagar tabelas nem desligar o RLS.
7. Painel mostra a empresa selecionada em banner fixo; operações críticas exigem digitar o código da empresa + MFA.

## 5. Modelo de dados (36 tabelas, migração `Inicial`)
Segurança: `tenants, stores, users, user_roles, user_permissions, user_sessions, devices, admin_users, audit_logs`
Módulos/assinatura: `module_catalog, module_dependencies, subscription_plans, plan_modules, subscriptions, tenant_modules, invoices, payment_events`
Suporte: `support_sessions, support_elevation_requests`
Identidade visual: `tenant_branding, branding_versions, branding_assets`
Operação: `categories, products, stock_movements, stock_balances, sales, sale_items, payments, cash_sessions, cash_movements, store_hours, operating_events, fiscal_documents, fiscal_events, sync_events`

Dinheiro em centavos (`long`), quantidades `numeric(18,3)`, custo histórico gravado em cada item vendido.
Tabelas do pedido **não criadas** por ainda não terem uso: `customers, loyalty_transactions, promotions, expenses, accounts_payable/receivable, service_orders, delivery_orders, suppliers, purchases, notifications, branding_overrides, module_versions, feature_limits` — entram com seus módulos.

## 6. Catálogo de módulos
Os 42 módulos do pedido estão no catálogo com dependências. O status reflete o que existe de verdade, e o servidor
**recusa habilitar módulo "Planejado"**.

| Status | Módulos |
|---|---|
| Disponível | PDV, Produtos, Estoque, Caixa, Horários, White Label |
| Beta | Fiscal (sem SEFAZ), Usuários avançados, Caixa por operador, Múltiplas lojas (só limites), App do proprietário (API pronta), Lucratividade (lucro e margem por produto e por dia) |
| Planejado (não habilitável) | os outros 30 (compras, financeiro, CRM, fidelidade, e-commerce, balanças, BI etc.) |

Etapa 4 (18/10/2026): janela de troco que só fecha com confirmação; Estoque com entrada de mercadoria, cadastro/
edição de produto com foto (armazenamento local ou Google Drive) e inventário por contagem; aviso de quebra de caixa
para o dono (app + push via Firebase quando configurado). Corrigido bug real: filtros de período enviados com
fuso -03:00 não encontravam vendas no SQLite (Histórico/Lucro vazios). Testes: 87 (SQLite) e 90 (PostgreSQL).

O PDV Windows tem área do dono (Painel, Histórico com cancelamento, Lucro, Estoque, Caixas). Cada aba só aparece
se a empresa contratou o módulo e o usuário tem a permissão — e o servidor revalida tudo. Capturas das telas
podem ser geradas com `Comercial.Desktop.exe --snapshot <pasta>` (dados de exemplo, sem servidor).

Dependências (exemplos): PDV → Produtos, Estoque, Caixa · Estoque → Produtos · Fiscal → PDV · Fidelidade → CRM, PDV ·
Compras → Fornecedores, Produtos, Estoque. Desativar módulo com dependente habilitado ou com pendência (caixa aberto,
documento fiscal pendente) é bloqueado com explicação de impacto; desativar **nunca apaga dados**.

Conceitos separados como pedido: **plano** (pacote/preço/limites, com *snapshot* na assinatura — mudar o plano não altera
contratos), **módulos habilitados** (por empresa, com vigência), **permissões** (por usuário), **licença** (estado
calculado), **estado operacional** (sincronização/dispositivos).

## 7. Licenciamento
- Estados: `active`, `expiring` (≤5 dias), `grace` (atraso dentro da tolerância), `restricted` (após tolerância), `suspended`, `cancelled`.
- `restricted/suspended/cancelled`: bloqueia **novas operações**; consulta, fechamento de caixa e regularização continuam. Nenhum dado é apagado.
- **Licença offline:** concessão JSON assinada com ECDSA P-256 (chave privada só no servidor), vinculada ao dispositivo, válida por 72 h (configurável). O PDV verifica assinatura, dispositivo, validade e **relógio retrocedido**. Editar o banco local não libera módulos (testado: adulteração e assinatura com chave do atacante falham).
- Falta de internet ≠ inadimplência: operações offline feitas enquanto a licença era válida são aceitas na sincronização.
- Pagamento só é confirmado por webhook autenticado (HMAC + janela anti-replay de 5 min + idempotência por evento + conferência de valor).

## 8. Painel do desenvolvedor (`src/Comercial.AdminWeb` + `/admin/v1`)
Feito: login com senha + TOTP (código não reutilizável), busca/criação de empresas, detalhe (assinatura, licença,
lojas, faturas, dispositivos, versões, último erro), módulos com análise de impacto + confirmação + MFA + motivo,
planos (consulta na tela; edição via API `PUT /admin/v1/plans`), suspensão/reativação, geração de fatura,
diagnóstico (saúde, dispositivos sem sync, rejeições, fiscais pendentes, eventos de segurança, integridade da
auditoria), auditoria por empresa, suporte, identidade visual. Papéis: `saas_admin` (tudo) e `support` (consulta e suporte).
Não feito: tela de edição de planos, gestão de administradores, alertas ativos (e-mail/push).

## 9. Suporte por cliente
Sessão por empresa com motivo e prazo (≤120 min), **somente consulta** por padrão (produtos, estoque, fiscal,
diagnóstico). Elevação: motivo + MFA do admin + **aprovação do proprietário** na API da loja + prazo (≤30 min) +
lista de operações. Vendas, caixa e emissão fiscal **nunca** são liberáveis ao suporte. A empresa vê e revoga
sessões; toda ação fica na auditoria com `SupportSessionId`. Não há conta compartilhada nem acesso oculto.

## 10. White Label
Configuração estruturada (cores hex, fontes aprovadas, estilos de botão/cartão, densidade, escala 0,85–1,3, textos
sem `< > { } \`, `javascript:` ou `url(`), contraste mínimo WCAG 4.5:1 validado, 6 temas predefinidos, rascunho →
prévia (não grava) → publicação versionada → restauração (gera nova versão, histórico preservado) → padrão.
Imagens: só PNG/JPEG reais (assinatura, cabeçalho, IEND/EOI), ≤2 MB, 16–4096 px, nome gerado pelo servidor, fora
do webroot, servidas só ao próprio tenant. Cache por `tenant:versão` (ETag + `Vary: Authorization`). Sem o módulo
White Label a empresa recebe a identidade padrão; a identidade nunca concede módulos.
Não feito: variação por filial, domínio próprio, reprocessamento (re-encode) das imagens.

## 11. Testes exigidos (seção 18) × resultado

| Requisito | Teste(s) | Resultado |
|---|---|---|
| Acesso entre tenants | `IsolationTests.*` (7 testes) | ✅ |
| Escalada de privilégios | `Gerente_nao_concede_papel_de_dono...`, `Gerente_nao_gerencia_usuario_de_nivel_superior`, `Operador_nao_...` | ✅ |
| Bypass de licença | `Licenca_vencida_apos_tolerancia...`, `Empresa_suspensa...`, `Licenca_offline_assinada_resiste...` | ✅ |
| Alteração de módulos sem autorização | `Alterar_modulo_exige_reautenticacao...`, `Admin_de_suporte_nao_altera...`, `Modulo_planejado...` | ✅ |
| Reutilização de tokens | `Reuso_de_refresh_token_revoga_toda_a_familia`, `Codigo_TOTP_nao_pode_ser_reutilizado`, `Access_token_antigo_invalido...` | ✅ |
| Força bruta | `Forca_bruta_bloqueia_conta...`, `Excesso_de_tentativas_de_login_retorna_429` | ✅ |
| Requisições duplicadas | `Venda_repetida...`, `Requisicoes_simultaneas_com_mesma_chave...` | ✅ |
| Manipulação de preços / descontos | `Preco_enviado_pelo_cliente_nunca_prevalece`, `Desconto_acima_do_limite...` | ✅ |
| Acesso indevido a relatórios | `Relatorio_financeiro_somente_com_permissao` | ✅ |
| Corrupção de estoque | `Vendas_concorrentes_nao_corrompem_saldo...` (20 vendas paralelas), `...movimentacoes_sao_imutaveis` | ✅ em SQLite |
| Manipulação de horários | `Horario_futuro_e_relogio_suspeito...` | ✅ |
| Sincronização duplicada | `Fluxo_offline_completo...` | ✅ |
| Reativação indevida de dispositivo | `Dispositivo_revogado_nao_loga_nao_sincroniza_e_nao_reativa` | ✅ |
| Webhooks duplicados/inválidos | `Webhook_de_pagamento_valida_assinatura_replay_valor_e_idempotencia` | ✅ |
| Manipulação do banco local | `Licenca_offline_assinada_resiste_a_adulteracao_do_banco_local` | ✅ |
| Acesso indevido ao painel | `Painel_admin_recusa_sem_token...`, `Token_admin_nao_acessa_api_de_loja...` | ✅ |
| Alteração de auditoria | `Auditoria_e_imutavel...`, `Alteracao_por_SQL_direto_quebra_a_cadeia_de_hash` | ✅ |
| Upload malicioso | `Upload_malicioso_e_recusado` (PHP, SVG com script, executável), `Upload_valida_tamanho...` | ✅ |
| Mistura de identidades / cache | `Identidade_visual_nao_mistura_entre_empresas_nem_por_cache`, `Asset_de_identidade_de_A...` | ✅ |
| Restauração de backups | `Backup_online_e_restauracao...` (SQLite), `PgDump_e_pgRestore_preservam_dados_RLS_e_cadeia_de_auditoria` (PostgreSQL) + `dev/pg-e2e.ps1` com os scripts de deploy | ✅ local (backup 0,4 s, restauração+verificação 2–8 s, base pequena) |
| Isolamento no banco (RLS) | `PostgresSecurityTests` (3 testes) | ✅ PostgreSQL 18 |
| Revogação de suporte | `Elevacao_exige_mfa_aprovacao...`, `Sessao_de_suporte_expirada...` | ✅ |
| Expiração de sessões | `Token_expirado_adulterado...`, `Usuario_bloqueado_perde_acesso...` | ✅ |
| Falhas de autorização offline | `Licenca_offline_expira_detecta_relogio...`, `Licenca_restrita_no_servidor...`, `Sync_com_token_de_outro_dispositivo...` | ✅ |
| Análise estática | compilador + analisadores padrão do .NET com avisos como erro | ⚠️ sem ferramenta SAST dedicada (ex.: CodeQL) |
| Ponta a ponta de interface | painel testado manualmente | ⚠️ sem testes E2E automatizados de UI |

**Bug real encontrado pelos testes e corrigido:** a resolução de dependências de módulos tratava dependência
compartilhada (PDV→Estoque→Produtos e PDV→Produtos) como ciclo e desligava o PDV. Corrigido e coberto por teste de regressão.

**Problemas encontrados só ao rodar em PostgreSQL real (etapa 2) e corrigidos:**
1. **RLS derrubava toda requisição sem tenant** (login, refresh): a função `app_tenant_ok` convertia `''` para `uuid`
   porque o PostgreSQL não garante curto-circuito no `AND`. Teria quebrado a produção inteira. Corrigido com `nullif` antes do cast.
2. **Horários com fuso (`-03:00`) seriam recusados** pelo `timestamptz` do Npgsql; um cliente no Brasil enviando hora
   local falharia. Agora todo `DateTimeOffset` é normalizado para UTC ao gravar (validado no `pg-e2e.ps1`).
3. **Script de RLS não era seguro em paralelo** (criação do papel `app_comercial`); agora é idempotente.
4. **Migrações não rodam mais na inicialização** em PostgreSQL por padrão (`Database:ApplyMigrationsOnStartup=false`):
   a API usa um usuário sem DDL e só recusa subir se houver migração pendente; a migração é etapa de deploy.

## 12. Riscos pendentes e mitigação
1. **PostgreSQL validado só localmente e com pouco volume.** Falta: rodar o job `postgres-integration` do CI, teste de carga, e medir o RTO real com uma base de tamanho de produção no ambiente gerenciado (PITR).
2. **Fiscal:** sem transmissão SEFAZ. Escolher provedor homologado (ou integração direta), implementar `IFiscalProvider`, homologar por UF. O PDV hoje não deve ser usado onde a emissão é obrigatória.
3. **Pagamentos:** adaptador por provedor real (formato de assinatura próprio) + emissão automática de faturas e notificações.
4. **Venda offline com preço divergente** é recusada e guardada para revisão — falta a tela de conciliação para o gerente.
5. **Abrir/fechar caixa exige conexão**; vendas offline exigem caixa aberto antes de cair a internet.
6. **Fuso horário:** "hoje" no dashboard é calculado em UTC; usar o fuso da loja (`Store.TimeZoneId`, já gravado).
7. **Escala da auditoria:** cadeia de hash global com trava consultiva — gargalo em alto volume; evoluir para cadeia por tenant + ancoragem periódica.
8. **Rate limiting em memória** por instância; em várias instâncias usar limite na borda (gateway/WAF) ou distribuído.
9. **Rotação da chave de licença:** o PDV aceita uma chave pública; suportar múltiplos `KeyId` antes de rotacionar.
10. **Imagens** validadas, mas não re-codificadas; adicionar reprocessamento para remover metadados/polyglots.
11. **Reautenticação TOTP** não tem proteção anti-replay (só o login tem).
12. **LGPD:** faltam exportação/anonimização por titular, política de retenção por tipo de dado e registro de finalidades (dependem do módulo CRM e de análise jurídica).
13. **App Flutter não compilado** e **telas WPF sem automação**; periféricos (impressora, gaveta, balança) não implementados.
14. Verificação de sessão no banco a cada requisição garante revogação imediata, mas custa uma consulta extra; medir e, se preciso, usar cache curto.

## 13. Custos operacionais e dependências externas (cotar — valores variam)
Hospedagem da API (contêiner/app service) e PostgreSQL gerenciado com PITR e réplica · armazenamento de backups
imutáveis em outra região · armazenamento de imagens · monitoramento/logs/alertas · domínio e certificados TLS ·
**provedor fiscal** (geralmente por documento ou mensalidade) e **certificado A1** de cada cliente · **gateway de
pagamento** (taxa por transação) · e-mail/SMS/WhatsApp oficial para notificações · **certificado de assinatura de
código** para o instalador Windows · **Apple Developer Program** (anual) e **Google Play** (taxa única) · pentest
externo periódico · assessoria jurídica LGPD/fiscal/trabalhista (escalas e ponto).

## 14. Como validar
```bash
dotnet test Comercial.sln
```
```bash
dotnet build Comercial.sln -c Release -warnaserror
```
Painel: `dev/run-api.ps1` + `dev/run-admin.ps1`, abrir http://localhost:5180, código MFA com `node dev/totp.js`.

## 15. Próximas etapas sugeridas (em ordem)
1. ~~PostgreSQL real: migração + RLS + suíte + restauração~~ — **feito localmente (etapa 2)**. Falta repetir no CI e no ambiente de homologação gerenciado, com volume realista.
2. Tela de conciliação de vendas offline recusadas + fuso horário por loja nos relatórios.
3. Integração fiscal (escolha do provedor, homologação NFC-e na UF piloto).
4. Provedor de pagamento real + cobrança recorrente automática.
5. Compilar e testar o app Flutter; flavors para ícone/nome por cliente quando vendido.
6. Instalador Windows assinado com atualização e reversão; periféricos (impressora térmica e gaveta).
7. Próximos módulos por prioridade comercial (sugestão: Fornecedores → Compras; CRM → Fidelidade; Financeiro).
