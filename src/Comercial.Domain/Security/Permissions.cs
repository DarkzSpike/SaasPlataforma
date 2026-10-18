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

    public static readonly string[] All =
    [
        ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust, SalesCreate, SalesRead,
        SalesCancel, SalesDiscountHigh, CashOperate, CashReview, OperatingOpenClose, OperatingCorrect,
        FiscalEmit, FiscalRead, ReportsRead, ReportsFinancial, UsersManage, DevicesManage, SupportApprove
    ];

    private static readonly Dictionary<string, string[]> RoleMap = new()
    {
        [Roles.Owner] = All,
        [Roles.Manager] =
        [
            ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust, SalesCreate, SalesRead,
            SalesCancel, SalesDiscountHigh, CashOperate, CashReview, OperatingOpenClose, OperatingCorrect,
            FiscalEmit, FiscalRead, ReportsRead, UsersManage, DevicesManage
        ],
        [Roles.Operator] = [ProductsRead, StockRead, SalesCreate, SalesRead, CashOperate, OperatingOpenClose, FiscalEmit],
        [Roles.Stockist] = [ProductsRead, ProductsWrite, StockRead, StockEntry, StockAdjust],
        [Roles.Finance] = [SalesRead, CashReview, FiscalRead, ReportsRead, ReportsFinancial],
    };

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
