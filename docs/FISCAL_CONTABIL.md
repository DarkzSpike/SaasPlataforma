# Módulo Fiscal e Contabilidade

## 1. Análise do sistema atual

| Item | O que existe |
|---|---|
| Stack | API .NET 10 (minimal APIs) + EF Core; PostgreSQL com RLS (SQLite nos testes); caixa WPF; painel web (JS); app Flutter |
| Vendas | `Sale`/`SaleItem`/`SalePayment` (dinheiro, débito, crédito, PIX, crediário, outros); cancelamento com motivo |
| Caixa | `CashSession` (abertura, esperado, contado, diferença, revisão) e `CashMovement` (suprimento, sangria, venda em dinheiro, cancelamento, recebimento) |
| Devoluções | `SaleReturn` (valor, forma de reembolso, retorno ao estoque) |
| PIX no caixa | `PosPixCharge` (cobrança confirmada pelo Mercado Pago → serve para conciliação) |
| Fiscal | NFC-e modelo 65 via provedor Focus NFe (`IFiscalProvider`): `FiscalDocument` com estado, chave, protocolo, número, série, links de DANFE/XML **no provedor**; `FiscalEvent` (histórico); `FiscalNumberVoid` (inutilização); emissão automática (`FiscalJob`) |
| Lacunas | **os XMLs originais não são guardados** (só o link do provedor); sem XML de cancelamento/inutilização; "denegada" tratada como rejeitada; pacote do contador atual só tem CSVs; sem fechamento mensal; e-mail sem anexo |
| E-mail | fila `EmailMessage` + SMTP configurado no painel, com novas tentativas |
| Segurança | permissões por papel, módulos por plano, auditoria append-only, RLS por empresa |

NF-e (55), CF-e-SAT e ECF **não são emitidos** pelo sistema hoje. O módulo trata o que existe (NFC-e) e deixa
os demais modelos como "não aplicável" até haver integração real — nada é simulado.

## 2. Plano em etapas

1. **Arquivo fiscal (prioridade máxima)** — tabela `fiscal_xml_files` imutável (conteúdo original, SHA-256,
   tipo: autorização/cancelamento/inutilização). Um serviço baixa o XML **do provedor fiscal** após a autorização,
   o cancelamento e a inutilização, valida (chave, protocolo, `cStat` de sucesso) e guarda. XML que não confere é
   recusado e vira pendência. Estado "denegada" separado de "rejeitada".
2. **Consulta de documentos** — lista paginada com filtros (período, situação, número, série, chave, modelo, loja),
   detalhes, download do XML guardado e abertura do DANFE.
3. **Exportação** — pacote ZIP por mês/loja com pastas `CNPJ/ano/mês/loja/NFCe/{autorizadas,eventos}`,
   inutilizações, relatórios CSV, `resumo_exportacao.txt` e `manifesto_sha256.txt`. Histórico de exportações
   (quem, quando, quais documentos), novo download com verificação de integridade, sem duplicar pacote idêntico.
4. **Relatórios contábeis** — fechamento de caixa (por caixa e consolidado), meios de pagamento (com conciliação
   PIX), vendas × documentos fiscais (com divergências e tributos lidos dos XMLs), cancelamentos, devoluções,
   rejeitados, pendentes, vendas por produto, movimentações financeiras. Tela + CSV (abre no Excel).
5. **Fechamento mensal** — 8 verificações com estados (pendente, em análise, concluído, com divergência,
   não aplicável), resumo, observações e conclusão auditada. Não altera nenhum documento fiscal.
6. **Contador** — cadastro do escritório, envio manual, envio automático mensal (respeitando as verificações),
   reenvio do mesmo pacote e histórico. O estado registrado é o que se sabe de fato: "aceito pelo servidor de
   e-mail" — nunca "entregue".
7. **Telas no caixa** — página "Fiscal e Contabilidade" com seções: Visão geral, Documentos, Exportação,
   Relatórios, Fechamento, Contador, Configurações.
8. **Testes** — exportação, eventos, períodos, ZIP, duplicidade, totais, divergências, envio/reenvio,
   histórico, permissões e falhas do provedor.

## 3. Dependências externas

| Funcionalidade | Depende de |
|---|---|
| Emitir NFC-e e obter XML oficial | CNPJ, certificado A1 e token do provedor fiscal (Focus NFe) configurados por empresa |
| Envio ao contador | SMTP configurado no painel do administrador (Configurações → E-mail) |
| Taxas/bandeiras de cartão | integração com a adquirente (não existe hoje) |
| NF-e 55, CF-e-SAT, ECF | integração específica (não existe hoje) |
| Validação das regras contábeis | o contador da empresa |

## 4. O que foi entregue

### Arquivos criados
| Arquivo | Conteúdo |
|---|---|
| `src/Comercial.Domain/Accounting.cs` | Entidades: `FiscalXmlFile` (imutável), `FiscalExport`, `FiscalMonthClosing`, `AccountantSettings`, `AccountantDelivery` |
| `src/Comercial.Api/Services/FiscalArchive.cs` | Arquivo fiscal: baixa do provedor, valida (chave, protocolo, cStat, sem DTD/XXE) e guarda os XMLs; consulta paginada e download |
| `src/Comercial.Api/Services/AccountingReports.cs` | Relatórios (caixa, pagamentos, conciliação PIX, vendas × notas, divergências, cancelamentos, devoluções, rejeitados, pendentes, produtos, financeiro), visão geral e CSV |
| `src/Comercial.Api/Services/AccountingClosing.cs` | Fechamento mensal, exportação ZIP, contador (envio manual/automático/reenvio/histórico) e serviço automático |
| `src/Comercial.Api/Endpoints/FiscalAccountingEndpoints.cs` | Rotas `/api/v1/fiscal/...` |
| `src/Comercial.Infrastructure/Migrations/*_FiscalContabilidade.cs` | Migração (5 tabelas novas + colunas) |
| `tests/Comercial.Tests/AccountingTests.cs` | 9 testes de ponta a ponta |

### Arquivos alterados
`Commerce.cs` (estado **denegada**, links dos XMLs de cancelamento/inutilização), `Messaging.cs` (anexo no e-mail),
`Permissions.cs` (`fiscal.export`, `fiscal.close`, `fiscal.send`), `FiscalService.cs` (download do XML só do host do
provedor, denegada, links de evento), `Email.cs` (anexo), `AppDbContext.cs`, `Program.cs`, `002_seguranca_rls.sql`
(RLS nas tabelas novas; `fiscal_xml_files` sem UPDATE/DELETE), `001_inicial.sql` (regerado), caixa:
`FiscalViewModel.cs` e `Views/FiscalView.xaml` (página com 7 abas), `Snapshot.cs`.

### Rotas
| Método | Rota | Permissão |
|---|---|---|
| GET | `/api/v1/fiscal/overview?from&to&storeId` | fiscal.read |
| GET | `/api/v1/fiscal/docs?from&to&status&number&series&key&model&storeId&page` | fiscal.read |
| GET | `/api/v1/fiscal/docs/{id}` · `/docs/{id}/xml?kind=autorizacao\|cancelamento` | fiscal.read · fiscal.export |
| POST | `/api/v1/fiscal/archive/fetch` (buscar XMLs que faltam no provedor) | fiscal.export |
| GET | `/api/v1/fiscal/reports/{chave}?from&to&storeId&format=csv` · `/reports/caixa/{id}` | fiscal.read |
| GET/POST | `/api/v1/fiscal/exports` · GET `/exports/{id}/download` | fiscal.read / fiscal.export |
| GET/POST | `/api/v1/fiscal/closing/{ano}/{mes}` · `/close` · `/reopen` | fiscal.read / fiscal.close |
| GET/PUT | `/api/v1/fiscal/accountant` · POST `/accountant/send` · GET `/accountant/deliveries` | fiscal.read / fiscal.send |

Todas exigem o módulo **fiscal** no plano. Papéis: dono (tudo), financeiro (ver, exportar, fechar, enviar),
gerente (ver e exportar), operador (nenhum).

## 5. Instalar, configurar e usar

1. Aplicar a migração: em produção, `deploy/postgres/001_inicial.sql` e depois `002_seguranca_rls.sql`
   (o `docker-compose` já faz isso); em desenvolvimento a API migra sozinha.
2. Configurações opcionais (`appsettings` ou variáveis de ambiente):
   - `Storage:Root` — onde ficam os ZIPs (`fiscal-exports/<empresa>/`). Os XMLs ficam no banco (entram no backup).
   - `Accountant:AutoRun` (padrão `true`), `Accountant:IntervalMinutes` (padrão 60), `Accountant:MaxAttachmentBytes` (padrão 15 MB).
3. SMTP: painel do administrador → e-mail (sem SMTP em desenvolvimento, o e-mail vira arquivo `.eml` em `dev-data/emails`).
4. No caixa: **Fiscal** → abas
   - **Visão geral**: indicadores e pendências do mês.
   - **Documentos**: filtros, detalhes, baixar XML, DANFE, transmitir, cancelar, "Buscar XMLs no provedor".
   - **Exportar XMLs**: "Gerar pacote do mês", histórico, baixar de novo, enviar ao contador.
   - **Relatórios**: escolha o relatório e "Exportar CSV (Excel)".
   - **Fechamento**: verificações, observações, "Concluir fechamento" / "Reabrir mês", salvar resumo.
   - **Contador**: cadastro do escritório, envio do mês, reenvio e histórico.
   - **Configurações**: emitente da NFC-e e inutilização.

## 6. Situação das funcionalidades

**Concluído e testado** (SQLite e PostgreSQL com RLS): arquivo dos XMLs originais e eventos com hash e sem
duplicidade; consulta paginada com filtros; exportação ZIP organizada com resumo, manifesto, pendências e
reaproveitamento de pacote idêntico; relatórios com CSV; divergências; fechamento com verificações, observações,
reabertura auditada; contador com envio manual, automático (bloqueado quando há pendência essencial), reenvio
sem gerar documentos de novo, histórico com o estado realmente conhecido; permissões; isolamento entre empresas.

**Depende de terceiros / validação**:
- XML oficial: só existe com o provedor fiscal configurado (CNPJ + certificado A1 + token). O download segue a
  documentação pública da Focus NFe (`caminho_xml_nota_fiscal`, `caminho_xml_cancelamento`, `caminho_xml`) e
  **ainda não foi testado com uma conta real**.
- Envio por e-mail: depende do SMTP; pacotes acima de 15 MB não vão como anexo (o sistema avisa).
- Taxas, bandeiras, parcelas e valor líquido de cartão: exigem integração com a adquirente (não existe).
- Vale-alimentação, vale-refeição e transferência não são formas de pagamento do caixa (entram em "Outros").
- NF-e 55, CF-e-SAT, ECF/Redução Z: não emitidos; aparecem como "não aplicável".
- Fechamento é por empresa (todas as lojas); a exportação e os relatórios filtram por loja. Perfil fiscal é
  um por empresa (filiais com CNPJ próprio precisariam de um perfil por loja).
- Os relatórios e o layout das pastas devem ser conferidos pelo contador da empresa.

## 7. Emissão direta na SEFAZ (sem provedor pago)

Além da Focus NFe, a NFC-e pode ser emitida **direto no webservice da SEFAZ** do estado, sem custo por nota, com a
biblioteca aberta **Unimake.DFe** (licença MIT). Arquivo: `src/Comercial.Api/Services/SefazDirect.cs`.

**Como funciona**
1. O caixa registra a venda (funciona offline) e sincroniza.
2. Logo depois, chama `POST /api/v1/fiscal/nfce/sale/{chave-da-venda}`: o servidor reserva o próximo número
   (sem duplicar, mesmo com vários caixas), monta o XML, **valida no schema oficial**, assina com o certificado A1 da
   loja, envia, guarda o XML autorizado (nfeProc) no arquivo fiscal e devolve os dados do DANFE.
3. O caixa imprime o **DANFE NFC-e** (térmica ESC/POS com QR Code nativo, ou impressora do Windows).
4. Sem internet ou sem resposta: sai o comprovante não fiscal e a nota segue na fila automática. Na nova tentativa o
   servidor **consulta a chave antes de reenviar** (não gera nota em duplicidade).
5. Cancelamento (evento 110111) e inutilização também vão direto e o XML do evento é arquivado na hora.

**Configuração por loja (caixa → Fiscal → Configurações)**
- "Dados do emitente": modo **Direto na SEFAZ**, CNPJ, IE, razão social, endereço com **código IBGE** do município,
  CEP, série, próximo número (para continuar a numeração de outro sistema), ambiente (comece em **homologação**).
- "Certificado digital": arquivo `.pfx` + senha. O sistema confere a senha, a validade e se o CNPJ do certificado é o
  da loja; guarda criptografado (Data Protection) e nunca devolve.
- CSC e ID do CSC: só necessários se o estado exigir QR Code versão 2 (o padrão da biblioteca é a versão 3).

**Configuração do servidor (opcional)**: responsável técnico, exigido em vários estados —
`Fiscal:RespTec:Cnpj`, `Fiscal:RespTec:Contato`, `Fiscal:RespTec:Email`, `Fiscal:RespTec:Fone`.
Exige um CNPJ de quem desenvolve o sistema; sem ele, alguns estados rejeitam a nota (rejeição 972).

**Limites atuais (conferir com o contador)**
- ICMS: Simples Nacional com CSOSN 102, 103, 300, 400 e 500; regime normal só CST 40, 41, 50 e 60 (sem alíquota no
  cadastro do produto ainda). PIS/COFINS: CST de não incidência (04–09) ou "outras" com valor zero.
- Produtos saem com "SEM GTIN"; consumidor não identificado (sem CPF na nota).
- Grupos da reforma tributária (IBS/CBS) não são enviados na emissão direta ainda.
- Tributos aproximados (Lei 12.741, tabela IBPT) não são impressos ainda.
- Contingência offline (tpEmis 9) não implementada: sem internet, a nota é emitida quando a conexão voltar.

**Testes**: `tests/Comercial.Tests/SefazDirectTests.cs` (8 testes) com certificado autoassinado de TESTE e uma SEFAZ
simulada — o XML real é montado, assinado e validado no schema oficial pela biblioteca. A transmissão de verdade só
pode ser conferida com o certificado e o CNPJ de uma loja, **em homologação**, estado por estado. Também falta testar
a biblioteca no servidor Linux.
