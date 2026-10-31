using System.Windows.Controls;

namespace Comercial.Desktop.Views;

// Code-behind mínimo: toda a lógica fica nos ViewModels.
public partial class ShellView : UserControl
{
    public ShellView() => InitializeComponent();

    // PasswordBox não aceita binding: a senha vai direto para o diálogo e é apagada quando ele muda.
    private void PromptPassword_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox pb && pb.DataContext is PromptViewModel p) p.Password = pb.Password;
    }

    private void PromptPassword_ContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox pb) pb.Clear();
    }
}
public partial class PosView : UserControl { public PosView() => InitializeComponent(); }
public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class HistoryView : UserControl { public HistoryView() => InitializeComponent(); }
public partial class ProfitView : UserControl { public ProfitView() => InitializeComponent(); }
public partial class StockView : UserControl { public StockView() => InitializeComponent(); }
public partial class CashView : UserControl { public CashView() => InitializeComponent(); }
public partial class PurchasesView : UserControl { public PurchasesView() => InitializeComponent(); }
public partial class CustomersView : UserControl { public CustomersView() => InitializeComponent(); }
public partial class FinanceView : UserControl { public FinanceView() => InitializeComponent(); }
public partial class PendingView : UserControl { public PendingView() => InitializeComponent(); }
public partial class AlertsView : UserControl { public AlertsView() => InitializeComponent(); }
public partial class InvoicesView : UserControl { public InvoicesView() => InitializeComponent(); }
public partial class SupportView : UserControl { public SupportView() => InitializeComponent(); }
public partial class EmployeesView : UserControl { public EmployeesView() => InitializeComponent(); }
public partial class StorePaymentsView : UserControl { public StorePaymentsView() => InitializeComponent(); }
public partial class FiscalView : UserControl { public FiscalView() => InitializeComponent(); }
public partial class DevicesView : UserControl { public DevicesView() => InitializeComponent(); }
public partial class PromotionsView : UserControl { public PromotionsView() => InitializeComponent(); }
public partial class QuotesView : UserControl { public QuotesView() => InitializeComponent(); }
public partial class ReportsView : UserControl { public ReportsView() => InitializeComponent(); }
public partial class AccountView : UserControl { public AccountView() => InitializeComponent(); }
public partial class HelpView : UserControl { public HelpView() => InitializeComponent(); }
public partial class SettingsView : UserControl { public SettingsView() => InitializeComponent(); }
public partial class CampaignsView : UserControl { public CampaignsView() => InitializeComponent(); }
