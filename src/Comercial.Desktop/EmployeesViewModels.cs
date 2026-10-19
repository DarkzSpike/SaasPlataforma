using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Comercial.Desktop.Core;

namespace Comercial.Desktop;

public sealed class EmployeeRow
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string Role { get; init; } = "";
    public string RoleName => EmployeesViewModel.RoleName(Role);
    public bool Active { get; init; }
    public string Status => Active ? "Ativo" : "Bloqueado";
    public bool IsMe { get; init; }
    public decimal Commission { get; init; }
}

public sealed class PermissionOption : ObservableObject
{
    private bool _checked;
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public bool CanGrant { get; init; }
    public bool FromRole { get; set; }
    public bool Checked { get => _checked; set => Set(ref _checked, value); }
    /// <summary>Não dá para conceder o que você mesmo não tem (o servidor também bloqueia).</summary>
    public bool Enabled => CanGrant || Checked;
    public string Hint => FromRole ? "padrão da função" : "";
}

public sealed record RoleOption(string Key, string Name);

/// <summary>
/// Funcionários: o dono (ou gerente) cria contas, escolhe a função e ajusta permissões individuais.
/// Regras do servidor: ninguém concede função ou permissão acima da própria; tudo auditado.
/// </summary>
public sealed class EmployeesViewModel(ShellViewModel shell) : OwnerPage(shell, "Funcionários", "Contas dos funcionários, função e o que cada um pode fazer")
{
    public ObservableCollection<EmployeeRow> Users { get; } = [];
    public ObservableCollection<PermissionOption> PermissionsList { get; } = [];
    public ObservableCollection<RoleOption> RoleOptions { get; } = [];
    private Dictionary<string, HashSet<string>> _rolePerms = [];
    private List<(Guid User, string Permission, bool Granted)> _grants = [];
    private EmployeeRow? _selected;
    private RoleOption? _role;
    private string _info = "";

    public string Info { get => _info; set { if (Set(ref _info, value)) Raise(nameof(HasInfo)); } }
    public bool HasInfo => Info.Length > 0;
    public bool HasSelection => Selected is not null;
    public bool CanEditSelected => Selected is { IsMe: false };
    public string DetailTitle => Selected is null ? "" : $"{Selected.Name} — {Selected.RoleName}";

    public EmployeeRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _role = value is null ? null : RoleOptions.FirstOrDefault(r => r.Key == value.Role);
            Raise(nameof(Role)); Raise(nameof(HasSelection)); Raise(nameof(CanEditSelected)); Raise(nameof(DetailTitle));
            BuildPermissions();
        }
    }

    public RoleOption? Role
    {
        get => _role;
        set { if (Set(ref _role, value)) BuildPermissions(keepGrants: false); }
    }

    public static string RoleName(string role) => role switch
    {
        "owner" => "Proprietário", "manager" => "Gerente", "operator" => "Operador de caixa", "stockist" => "Estoquista", "finance" => "Financeiro", _ => role,
    };

    public RelayCommand NewCommand => RelayCommand.Sync(_ => AskNew());
    public RelayCommand SaveCommand => new(async _ => await SaveAsync(), _ => CanEditSelected);
    public RelayCommand BlockCommand => new(async _ => await Do(async () =>
    {
        var s = Selected!;
        await S.Api.PostAsync($"/api/v1/users/{s.Id}/{(s.Active ? "block" : "unblock")}", new { });
        Info = s.Active ? $"{s.Name} foi bloqueado e desconectado." : $"{s.Name} foi desbloqueado.";
    }), _ => CanEditSelected);
    public RelayCommand CommissionCommand => RelayCommand.Sync(_ =>
    {
        var s = Selected!;
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Comissão de " + s.Name, Message = $"Percentual sobre as vendas dele (líquidas de devoluções). Atual: {s.Commission:0.##}%. Aparece em Relatórios → Vendedores e comissões.",
            ValueLabel = "Comissão (%)", ConfirmText = "Salvar",
        }, async p =>
        {
            if (!decimal.TryParse(p.Value.Replace("%", "").Trim(), System.Globalization.NumberStyles.Number, AppServices.PtBr, out var pct) || pct < 0 || pct > 50) { p.Error = "Informe de 0 a 50."; return false; }
            try { await S.Api.PutAsync($"/api/v1/users/{s.Id}/commission", new { percent = pct }); await LoadAsync(); Info = $"Comissão de {s.Name}: {pct:0.##}%."; return true; }
            catch (ApiException ex) { p.Error = "Falha: " + ex.Code; }
            catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }, _ => Selected is not null);

    public RelayCommand LogoutUserCommand => new(async _ => await Do(async () =>
    {
        await S.Api.PostAsync($"/api/v1/users/{Selected!.Id}/revoke-sessions", new { });
        Info = $"{Selected!.Name} foi desconectado de todos os aparelhos.";
    }), _ => CanEditSelected);

    /// <summary>Funcionário perdeu o celular do autenticador: remove a verificação em duas etapas (fica na auditoria).</summary>
    public RelayCommand ResetTwoFactorCommand => new(async _ => await Do(async () =>
    {
        await S.Api.PostAsync($"/api/v1/users/{Selected!.Id}/2fa/reset", new { });
        Info = $"Verificação em duas etapas de {Selected!.Name} removida. Ele entra só com a senha e pode ativar de novo em Minha conta.";
    }), _ => CanEditSelected);
    public RelayCommand ResetPasswordCommand => RelayCommand.Sync(_ =>
    {
        var s = Selected!;
        Shell.ShowPrompt(new PromptViewModel
        {
            Title = "Nova senha para " + s.Name,
            Message = "Mínimo de 10 caracteres, com letras e números. O funcionário será desconectado e entrará com a nova senha.",
            PasswordLabel = "Nova senha", ConfirmText = "Definir senha",
        }, async p =>
        {
            try
            {
                await S.Api.PostAsync($"/api/v1/users/{s.Id}/password", new { newPassword = p.Password });
                Info = $"Senha de {s.Name} alterada.";
                return true;
            }
            catch (ApiException ex) { p.Error = ex.Code == "weak_password" ? "Senha fraca: use 10+ caracteres com letras e números." : "Falha: " + ex.Code; }
            catch (HttpRequestException) { p.Error = "Sem conexão com o servidor."; }
            finally { p.Password = ""; }
            return false;
        });
    }, _ => CanEditSelected);

    private async Task Do(Func<Task> action)
    {
        Error = ""; Info = "";
        var keepId = Selected?.Id;
        try
        {
            await action();
            var msg = Info;
            await LoadAsync();
            Info = msg;
            Selected = Users.FirstOrDefault(u => u.Id == keepId);
        }
        catch (ApiException ex) { Error = Message(ex); }
        catch (HttpRequestException) { Error = "Sem conexão com o servidor."; }
    }

    private static string Message(ApiException ex) => ex.Code switch
    {
        "privilege_escalation" => "Você não pode dar uma função ou permissão acima da sua.",
        "module_not_enabled" => "Permissões individuais exigem o módulo Usuários e Permissões Avançadas. A função continua valendo.",
        "user_limit" => "Limite de usuários do seu plano atingido.",
        "email_taken" => "Este e-mail já está cadastrado.",
        "weak_password" => "Senha fraca: use 10+ caracteres com letras e números.",
        "self_change" => "Você não pode alterar a sua própria conta por aqui.",
        _ => "Não foi possível concluir: " + ex.Code,
    };

    private void BuildPermissions(bool keepGrants = true)
    {
        var current = PermissionsList.ToDictionary(p => p.Key, p => p);
        var basePerms = _role is not null && _rolePerms.TryGetValue(_role.Key, out var rp) ? rp : [];
        var grants = keepGrants && Selected is not null ? _grants.Where(g => g.User == Selected.Id).ToList() : [];
        foreach (var p in PermissionsList)
        {
            p.FromRole = basePerms.Contains(p.Key);
            var g = grants.FirstOrDefault(x => x.Permission == p.Key);
            p.Checked = g.Permission is not null ? g.Granted : p.FromRole;
        }
        // Recria a lista para atualizar "padrão da função" e o estado de habilitado.
        var copy = PermissionsList.Select(p => new PermissionOption { Key = p.Key, Name = p.Name, CanGrant = p.CanGrant, FromRole = p.FromRole, Checked = p.Checked }).ToList();
        PermissionsList.Clear();
        foreach (var p in copy) PermissionsList.Add(p);
    }

    private async Task SaveAsync()
    {
        var s = Selected;
        if (s is null || _role is null) return;
        await Do(async () =>
        {
            if (_role.Key != s.Role) await S.Api.PutAsync($"/api/v1/users/{s.Id}/roles", new { roles = new[] { _role.Key } });
            var basePerms = _rolePerms.GetValueOrDefault(_role.Key) ?? [];
            // Só guarda o que difere do padrão da função.
            var grants = PermissionsList.Where(p => p.Checked != basePerms.Contains(p.Key)).Select(p => new { permission = p.Key, granted = p.Checked }).ToList();
            var hadGrants = _grants.Any(g => g.User == s.Id);
            if (grants.Count > 0 || hadGrants) await S.Api.PutAsync($"/api/v1/users/{s.Id}/permissions", grants);
            Info = $"Permissões de {s.Name} salvas.";
        });
    }

    private void AskNew()
    {
        var form = new FormViewModel { Title = "Novo funcionário", Message = "Ele entra no PDV com o código da empresa, este e-mail e a senha inicial.", ConfirmText = "Criar conta" };
        form.Fields.Add(new FormField("name", "Nome", icon: ""));
        form.Fields.Add(new FormField("email", "E-mail (login)", icon: ""));
        form.Fields.Add(new FormField("password", "Senha inicial (10+ caracteres, letras e números)", icon: ""));
        form.Fields.Add(new FormField("role", "Função", RoleOptions.FirstOrDefault(r => r.Key == "operator")?.Name ?? RoleOptions.First().Name, options: RoleOptions.Select(r => r.Name).ToList()));
        Shell.ShowForm(form, async f =>
        {
            var role = RoleOptions.FirstOrDefault(r => r.Name == f["role"]);
            if (f["name"].Length < 2 || !f["email"].Contains('@') || role is null) { f.Error = "Preencha nome, e-mail e função."; return false; }
            try
            {
                await S.Api.PostAsync("/api/v1/users", new { email = f["email"], displayName = f["name"], password = f["password"], roles = new[] { role.Key } });
                await LoadAsync();
                Info = $"Conta de {f["name"]} criada como {role.Name}. Ajuste as permissões ao lado, se quiser.";
                Selected = Users.FirstOrDefault(u => u.Email == f["email"].ToLowerInvariant());
                return true;
            }
            catch (ApiException ex) { f.Error = Message(ex); }
            catch (HttpRequestException) { f.Error = "Sem conexão com o servidor."; }
            return false;
        });
    }

    protected override async Task LoadCoreAsync()
    {
        var cat = await S.Api.GetAsync("/api/v1/users/permission-catalog");
        _rolePerms = cat.GetProperty("roles").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!,
            r => r.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToHashSet());
        RoleOptions.Clear();
        foreach (var k in new[] { "operator", "stockist", "finance", "manager", "owner" }.Where(_rolePerms.ContainsKey)) RoleOptions.Add(new RoleOption(k, RoleName(k)));
        PermissionsList.Clear();
        foreach (var p in cat.GetProperty("permissions").EnumerateArray())
            PermissionsList.Add(new PermissionOption { Key = p.GetProperty("key").GetString()!, Name = p.GetProperty("name").GetString()!, CanGrant = p.GetProperty("canGrant").GetBoolean() });

        _grants = (await S.Api.GetAsync("/api/v1/users/grants")).EnumerateArray()
            .Select(g => (g.GetProperty("userId").GetGuid(), g.GetProperty("permission").GetString()!, g.GetProperty("granted").GetBoolean())).ToList();

        var keep = Selected?.Id;
        Users.Clear();
        foreach (var u in (await S.Api.GetAsync("/api/v1/users")).EnumerateArray())
        {
            var roles = u.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!).ToList();
            var main = new[] { "owner", "manager", "finance", "stockist", "operator" }.FirstOrDefault(roles.Contains) ?? roles.FirstOrDefault() ?? "";
            Users.Add(new EmployeeRow
            {
                Id = u.GetProperty("id").GetGuid(), Name = u.GetProperty("displayName").GetString()!, Email = u.GetProperty("email").GetString()!,
                Role = main, Active = u.GetProperty("isActive").GetBoolean(), IsMe = u.GetProperty("id").GetGuid() == S.Session.UserId,
                Commission = u.TryGetProperty("commissionPercent", out var cp) && cp.ValueKind == JsonValueKind.Number ? cp.GetDecimal() : 0,
            });
        }
        _selected = null;
        Selected = Users.FirstOrDefault(u => u.Id == keep) ?? Users.FirstOrDefault(u => !u.IsMe);
    }
}
