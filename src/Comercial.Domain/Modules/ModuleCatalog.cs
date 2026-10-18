namespace Comercial.Domain.Modules;

public enum ModuleStatus
{
    /// <summary>Apenas planejado — não pode ser habilitado para clientes.</summary>
    Planned = 0,
    /// <summary>Implementado parcialmente; habilitável somente por decisão explícita.</summary>
    Beta = 1,
    Released = 2,
    Deprecated = 3
}

public class ModuleDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public ModuleStatus Status { get; set; }
    public string Version { get; set; } = "1.0.0";
    public bool AvailableAsAddOn { get; set; } = true;
    /// <summary>Módulos centrais ficam sempre ativos (autenticação, usuários básicos, licença).</summary>
    public bool IsCore { get; set; }
    public List<ModuleDependency> Dependencies { get; set; } = [];
}

public class ModuleDependency
{
    public string ModuleKey { get; set; } = "";
    public string DependsOnKey { get; set; } = "";
}

public static class ModuleKeys
{
    public const string Core = "core";
    public const string Pdv = "pdv";
    public const string Products = "produtos";
    public const string Stock = "estoque";
    public const string Cash = "caixa";
    public const string Fiscal = "fiscal";
    public const string Suppliers = "fornecedores";
    public const string Purchases = "compras";
    public const string PurchaseImport = "importacao_compras";
    public const string Finance = "financeiro";
    public const string PayablesReceivables = "contas";
    public const string Expenses = "despesas";
    public const string Profitability = "lucratividade";
    public const string Crm = "crm";
    public const string Loyalty = "fidelidade";
    public const string Coupons = "cupons";
    public const string Campaigns = "campanhas";
    public const string UsersAdvanced = "usuarios_avancado";
    public const string StoreHours = "horarios";
    public const string CashPerOperator = "caixa_operador";
    public const string Shifts = "escalas";
    public const string Commissions = "comissoes";
    public const string Returns = "trocas";
    public const string ServiceOrders = "ordens_servico";
    public const string Quotes = "orcamentos";
    public const string Delivery = "entrega";
    public const string Ecommerce = "ecommerce";
    public const string Picking = "expedicao";
    public const string Peripherals = "perifericos";
    public const string Scales = "balancas";
    public const string Labels = "etiquetas";
    public const string MultiStore = "multilojas";
    public const string InventoryAdvanced = "inventario";
    public const string LossPrevention = "prevencao_perdas";
    public const string Bi = "bi";
    public const string OwnerApp = "app_proprietario";
    public const string Integrations = "integracoes";
    public const string BackupAdvanced = "backup_avancado";
    public const string CustomerPortal = "portal_cliente";
    public const string WhiteLabel = "white_label";
    public const string CustomDomain = "dominio";
    public const string ReportsPremium = "relatorios_premium";
    public const string Notifications = "notificacoes";
}

/// <summary>
/// Catálogo inicial. O estado reflete o que REALMENTE está implementado neste código:
/// módulos "Planned" existem no catálogo comercial, mas o servidor recusa habilitá-los.
/// </summary>
public static class ModuleCatalogSeed
{
    private static ModuleDefinition M(string key, string name, string category, ModuleStatus status, string desc, params string[] deps) => new()
    {
        Key = key, Name = name, Category = category, Status = status, Description = desc,
        Dependencies = deps.Select(d => new ModuleDependency { ModuleKey = key, DependsOnKey = d }).ToList()
    };

    public static List<ModuleDefinition> Build()
    {
        var P = ModuleStatus.Planned; var B = ModuleStatus.Beta; var R = ModuleStatus.Released;
        var list = new List<ModuleDefinition>
        {
            new() { Key = ModuleKeys.Core, Name = "Núcleo", Category = "Plataforma", Status = R, IsCore = true, AvailableAsAddOn = false,
                    Description = "Autenticação, usuários básicos, dispositivos, licença e identidade visual publicada." },
            // A — Operação essencial
            M(ModuleKeys.Pdv, "PDV e vendas", "A - Operação essencial", R, "Vendas, pagamentos múltiplos, cancelamentos auditados, idempotência.", ModuleKeys.Products, ModuleKeys.Stock, ModuleKeys.Cash),
            M(ModuleKeys.Products, "Produtos e catálogo", "A - Operação essencial", R, "Cadastro de produtos, SKU, código de barras, preço e custo."),
            M(ModuleKeys.Stock, "Estoque", "A - Operação essencial", R, "Saldos por loja, entradas, ajustes auditados, histórico.", ModuleKeys.Products),
            M(ModuleKeys.Cash, "Caixa financeiro", "A - Operação essencial", R, "Abertura, suprimento, sangria, fechamento e diferenças."),
            M(ModuleKeys.Fiscal, "Fiscal brasileiro", "A - Operação essencial", B, "Registro de documentos fiscais e estados. Transmissão SEFAZ depende de provedor homologado (não incluído).", ModuleKeys.Pdv),
            // B — Compras
            M(ModuleKeys.Suppliers, "Fornecedores", "B - Compras", P, "Cadastro e histórico de fornecedores."),
            M(ModuleKeys.Purchases, "Compras avançadas", "B - Compras", P, "Pedidos, aprovação e recebimento.", ModuleKeys.Suppliers, ModuleKeys.Products, ModuleKeys.Stock),
            M(ModuleKeys.PurchaseImport, "Importação de documentos de compra", "B - Compras", P, "Importação de XML de NF-e de entrada.", ModuleKeys.Purchases),
            // C — Financeiro
            M(ModuleKeys.Finance, "Financeiro avançado", "C - Financeiro", P, "Fluxo de caixa, conciliação, centros de custo.", ModuleKeys.Cash),
            M(ModuleKeys.PayablesReceivables, "Contas a pagar e receber", "C - Financeiro", P, "Vencimentos, parcelas, baixas.", ModuleKeys.Finance),
            M(ModuleKeys.Expenses, "Despesas e custos", "C - Financeiro", P, "Despesas operacionais e comprovantes.", ModuleKeys.Finance),
            M(ModuleKeys.Profitability, "Lucratividade", "C - Financeiro", P, "Margens com custo histórico.", ModuleKeys.Pdv),
            // D — Clientes
            M(ModuleKeys.Crm, "CRM", "D - Clientes", P, "Cadastro de clientes e consentimentos."),
            M(ModuleKeys.Loyalty, "Fidelidade", "D - Clientes", P, "Pontos e resgates.", ModuleKeys.Crm, ModuleKeys.Pdv),
            M(ModuleKeys.Coupons, "Cupons e promoções", "D - Clientes", P, "Cupons, combos, regras por período.", ModuleKeys.Pdv),
            M(ModuleKeys.Campaigns, "Campanhas", "D - Clientes", P, "Campanhas com controle de consentimento.", ModuleKeys.Crm),
            // E — Funcionários
            M(ModuleKeys.UsersAdvanced, "Usuários e permissões avançadas", "E - Funcionários", B, "Permissões individuais, restrição por loja, revogação de sessões."),
            M(ModuleKeys.StoreHours, "Horários de funcionamento", "E - Funcionários", R, "Abertura/fechamento real, offline e correções auditáveis."),
            M(ModuleKeys.CashPerOperator, "Caixa por operador", "E - Funcionários", B, "Caixa vinculado ao operador e relatórios.", ModuleKeys.Cash),
            M(ModuleKeys.Shifts, "Escalas e presença", "E - Funcionários", P, "Escalas e presença (requer análise trabalhista)."),
            M(ModuleKeys.Commissions, "Comissões", "E - Funcionários", P, "Comissões por venda.", ModuleKeys.Pdv),
            // F — Atendimento
            M(ModuleKeys.Returns, "Trocas e devoluções", "F - Atendimento", P, "Devoluções e estornos.", ModuleKeys.Pdv),
            M(ModuleKeys.ServiceOrders, "Ordens de serviço", "F - Atendimento", P, "OS com peças e status.", ModuleKeys.Products),
            M(ModuleKeys.Quotes, "Orçamentos e pedidos", "F - Atendimento", P, "Orçamentos convertíveis em venda.", ModuleKeys.Pdv),
            // G — Logística
            M(ModuleKeys.Delivery, "Entrega e retirada", "G - Logística", P, "Pedidos de entrega.", ModuleKeys.Pdv),
            M(ModuleKeys.Ecommerce, "E-commerce", "G - Logística", P, "Integração com lojas virtuais.", ModuleKeys.Products, ModuleKeys.Stock),
            M(ModuleKeys.Picking, "Separação e expedição", "G - Logística", P, "Listas de separação.", ModuleKeys.Delivery),
            // H — Equipamentos
            M(ModuleKeys.Peripherals, "Impressoras e periféricos", "H - Equipamentos", P, "Leitores, impressoras, gavetas.", ModuleKeys.Pdv),
            M(ModuleKeys.Scales, "Balanças", "H - Equipamentos", P, "Produtos por peso.", ModuleKeys.Pdv),
            M(ModuleKeys.Labels, "Etiquetas e precificação", "H - Equipamentos", P, "Modelos e lotes de etiquetas.", ModuleKeys.Products),
            // I — Gestão avançada
            M(ModuleKeys.MultiStore, "Múltiplas lojas", "I - Gestão avançada", B, "Filiais e estoque por unidade (limite de lojas do plano).", ModuleKeys.Stock),
            M(ModuleKeys.InventoryAdvanced, "Inventário avançado", "I - Gestão avançada", P, "Contagens cegas e aprovação.", ModuleKeys.Stock),
            M(ModuleKeys.LossPrevention, "Prevenção de perdas", "I - Gestão avançada", P, "Alertas para revisão humana.", ModuleKeys.Pdv, ModuleKeys.Stock),
            M(ModuleKeys.Bi, "BI e relatórios avançados", "I - Gestão avançada", P, "Comparativos e indicadores.", ModuleKeys.Pdv),
            // J — SaaS
            M(ModuleKeys.OwnerApp, "Aplicativo do proprietário", "J - Serviços SaaS", B, "Dashboard móvel (API pronta; app Flutter não compilado neste ambiente).", ModuleKeys.Pdv),
            M(ModuleKeys.Integrations, "Integrações externas", "J - Serviços SaaS", P, "APIs de parceiros."),
            M(ModuleKeys.BackupAdvanced, "Backup avançado", "J - Serviços SaaS", P, "Monitoramento e testes de restauração."),
            M(ModuleKeys.CustomerPortal, "Portal do cliente SaaS", "J - Serviços SaaS", P, "Assinatura e solicitações."),
            M(ModuleKeys.WhiteLabel, "Identidade visual White Label", "J - Serviços SaaS", R, "Logotipos, cores, temas, publicação versionada."),
            M(ModuleKeys.CustomDomain, "Domínio personalizado", "J - Serviços SaaS", P, "Domínio próprio com verificação.", ModuleKeys.WhiteLabel),
            M(ModuleKeys.ReportsPremium, "Relatórios e exportações premium", "J - Serviços SaaS", P, "Agendamento e modelos."),
            M(ModuleKeys.Notifications, "Notificações inteligentes", "J - Serviços SaaS", P, "Alertas com regras de frequência."),
        };
        return list;
    }
}
