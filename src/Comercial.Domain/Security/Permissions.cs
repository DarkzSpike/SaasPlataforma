namespace Comercial.Domain.Security;

public static class Roles
{
    public const string Owner = "owner";
    public const string Manager = "manager";
    public const string Operator = "operator";
    public const string Stockist = "stockist";
    public const string Finance = "finance";

    public static readonly string[] All = [Owner, Manager, Operator, Stockist, Finance];

    /// <summary>Nível hierárquico: um usuário só atribui papéis de nível menor ou igual ao seu.</summary>
    public static int Level(string role) => role switch
    {
        Owner => 100,
        Manager => 50,
        Finance => 30,
        Stockist => 20,
        Operator => 10,
        _ => 0
    };
}

public static class Permissions
{
    public const string ProductsRead = "products.read";
    public const string ProductsWrite = "products.write";
    public const string StockRead = "stock.read";
    public const string StockEntry = "stock.entry";
    public const string StockAdjust = "stock.adjust";
    public const string SalesCreate = "sales.create";
    public const string SalesRead = "sales.read";
    public const string SalesCancel = "sales.cancel";
    public const string SalesDiscountHigh = "sales.discount.high";
    public const string CashOperate = "cash.operate";
    public const string CashReview = "cash.review";
    public const string OperatingOpenClose = "operating.openclose";
    public const string OperatingCorrect = "operating.correct";
    public const string FiscalEmit = "fiscal.emit";
    public const string FiscalRead = "fiscal.read";
    public const string ReportsRead = "reports.read";
    public const string ReportsFinancial = "reports.financial";
    public const string UsersManage = "users.manage";
    public const string DevicesManage = "devices.manage";
    public const string SupportApprove = "support.approve";
    /// <summary>Ver e pagar as faturas do sistema (mensalidade).</summary>
    public const string BillingManage = "billing.manage";
    /// <summary>Autorizar desconto no valor total da venda (por padrão só o proprietário).</summary>
    public const string SalesDiscountAuthorize = "sales.discount.authorize";
    /// <summary>Configurar a conta de recebimento da loja (Mercado Pago, PIX no caixa).</summary>
    public const string PaymentsConfigure = "payments.configure";

    /// <summary>Nomes em português para a tela de funcionários.</summary>
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [ProductsRead] = "Ver produtos", [ProductsWrite] = "Cadastrar e editar produtos", [StockRead] = "Ver estoque",
        [StockEntry] = "Lançar entrada de mercadoria", [StockAdjust] = "Ajustar estoque e inventário", [SalesCreate] = "Vender (PDV)",
        [SalesRead] = "Ver histórico de vendas", [SalesCancel] = "Cancelar vendas", [SalesDiscountHigh] = "Desconto por item acima do limite",
        [SalesDiscountAuthorize] = "Autorizar desconto no total da venda", [SalesReturn] = "Trocas e devoluções",
        [CashOperate] = "Abrir/fechar o próprio caixa e sangria", [CashReview] = "Ver e revisar caixas de todos",
        [OperatingOpenClose] = "Abrir e fechar a loja", [OperatingCorrect] = "Corrigir horários da loja",
        [FiscalEmit] = "Emitir documento fiscal", [FiscalRead] = "Ver documentos fiscais",
        [ReportsRead] = "Ver painel e relatórios", [ReportsFinancial] = "Ver lucro e avisos financeiros", [ReportsExport] = "Exportar relatórios (CSV)",
        [UsersManage] = "Gerenciar funcionários", [DevicesManage] = "Gerenciar terminais", [SupportApprove] = "Aprovar acesso do suporte",
        [SyncReconcile] = "Resolver pendências de sincronização", [SuppliersManage] = "Fornecedores", [PurchasesManage] = "Pedidos de compra",
        [PurchasesApprove] = "Aprovar pedidos de compra", [PurchasesReceive] = "Receber mercadoria de compras",
        [CustomersRead] = "Ver clientes", [CustomersManage] = "Cadastrar clientes", [CustomersPrivacy] = "Exportar/anonimizar dados de clientes (LGPD)",
        [LoyaltyManage] = "Configurar programa de pontos", [FinanceRead] = "Ver contas a pagar/receber", [FinanceManage] = "Lançar e baixar contas",
        [BillingManage] = "Ver e pagar as faturas do sistema",
        [PaymentsConfigure] = "Configurar recebimentos da loja (PIX no caixa)",
    };
    public const string SalesReturn = "sales.return";
    /// <summary>Resolver vendas offline recusadas (reenviar com preço atual ou descartar com motivo).</summary>
    public const string SyncReconcile = "sync.reconcile";
    public const string SuppliersManage = "suppliers.manage";
    public const string PurchasesManage = "purchases.manage";
    public const string PurchasesApprove = "purchases.approve";
    public const string PurchasesReceive = "purchases.receive";
    public const string CustomersRead = "customers.read";
    public const string CustomersManage = "customers.manage";
    /// <summary>Exportar ou anonimizar dados pessoais de clientes (LGPD).</summary>
    public const string CustomersPrivacy = "customers.privacy";
    public const string LoyaltyManage = "loyalty.manage";
    public const string FinanceRead = "finance.read";
    public const string FinanceManage = "finance.manage";
    public const string ReportsExport = "reports.export";

    public static readonly string[] All =
    [
        ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust, SalesCreate, SalesRead,
        SalesCancel, SalesDiscountHigh, CashOperate, CashReview, OperatingOpenClose, OperatingCorrect,
        FiscalEmit, FiscalRead, ReportsRead, ReportsFinancial, UsersManage, DevicesManage, SupportApprove,
        SalesReturn, SyncReconcile, SuppliersManage, PurchasesManage, PurchasesApprove, PurchasesReceive,
        CustomersRead, CustomersManage, CustomersPrivacy, LoyaltyManage, FinanceRead, FinanceManage, ReportsExport, BillingManage, SalesDiscountAuthorize, PaymentsConfigure
    ];

    private static readonly Dictionary<string, string[]> RoleMap = new()
    {
        [Roles.Owner] = All,
        [Roles.Manager] =
        [
            ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust, SalesCreate, SalesRead,
            SalesCancel, SalesDiscountHigh, CashOperate, CashReview, OperatingOpenClose, OperatingCorrect,
            FiscalEmit, FiscalRead, ReportsRead, UsersManage, DevicesManage,
            SalesReturn, SyncReconcile, SuppliersManage, PurchasesManage, PurchasesApprove, PurchasesReceive,
            CustomersRead, CustomersManage, FinanceRead, ReportsExport
        ],
        [Roles.Operator] = [ProductsRead, StockRead, SalesCreate, SalesRead, CashOperate, OperatingOpenClose, FiscalEmit, CustomersRead, CustomersManage],
        [Roles.Stockist] = [ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust, SuppliersManage, PurchasesManage, PurchasesReceive],
        [Roles.Finance] = [SalesRead, CashReview, FiscalRead, ReportsRead, ReportsFinancial, FinanceRead, FinanceManage, ReportsExport, SuppliersManage, BillingManage],
    };

    public static IReadOnlyList<string> ForRole(string role) => RoleMap.TryGetValue(role, out var p) ? p : [];

    public static IReadOnlySet<string> Resolve(IEnumerable<string> roles, IEnumerable<(string Permission, bool Granted)> grants)
    {
        var set = new HashSet<string>(roles.SelectMany(r => RoleMap.TryGetValue(r, out var p) ? p : []));
        foreach (var (permission, granted) in grants)
        {
            if (!All.Contains(permission)) continue;
            if (granted) set.Add(permission); else set.Remove(permission);
        }
        return set;
    }
}

/// <summary>Papéis da equipe do SaaS (painel do desenvolvedor). Separados dos papéis das lojas.</summary>
public static class AdminRoles
{
    public const string SaasAdmin = "saas_admin";
    public const string Support = "support";
}
