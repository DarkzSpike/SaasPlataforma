namespace Comercial.Desktop;

/// <summary>Ajuda rápida dentro do caixa (funciona sem internet). Abre pelo menu ou pela tecla F1.</summary>
public sealed class HelpViewModel(ShellViewModel shell) : OwnerPage(shell, "Ajuda", "Como usar o caixa no dia a dia")
{
    public sealed record Topic(string Title, string Text);

    public string VersionText => Shell.VersionText;

    public IReadOnlyList<Topic> Topics { get; } =
    [
        new("Atalhos do caixa",
            "F10 — finalizar a venda.\nF1 — abrir esta ajuda.\nEnter no campo de busca — adiciona o produto (código de barras, código interno ou nome).\n" +
            "Quantidade: digite 3* antes do código (ex.: 3*7891234567890) para lançar 3 unidades.\n" +
            "Produtos vendidos por peso (KG): passe a etiqueta da balança ou, com balança ligada no computador, o peso é lido sozinho."),
        new("Abrir e fechar o dia",
            "1. Na tela de Vendas, clique em Abrir loja e depois abra o caixa informando o dinheiro inicial (troco).\n" +
            "2. Durante o dia: Sangria retira dinheiro com segurança (o dono recebe aviso no celular); Suprimento coloca troco.\n" +
            "3. No fim: feche o caixa contando o dinheiro. Diferença para menos aparece como quebra de caixa no Painel. Só depois feche a loja."),
        new("Formas de pagamento",
            "Dá para dividir a venda em várias formas (ex.: parte no PIX, parte em dinheiro).\n" +
            "PIX com QR Code: ative em Recebimentos com a conta Mercado Pago da loja — a venda confirma sozinha quando o cliente paga.\n" +
            "Fiado (crediário): escolha o cliente; respeita o limite definido em Clientes. Recebimentos de fiado entram no caixa."),
        new("Sem internet",
            "O caixa continua vendendo sem internet (dinheiro, cartão na maquininha, PIX lançado manualmente). As vendas ficam guardadas " +
            "criptografadas neste computador e são enviadas sozinhas quando a conexão volta (ícone de nuvem no topo).\n" +
            "Vendas recusadas pelo servidor aparecem em Pendências para o gerente resolver — nada é apagado."),
        new("Descontos e autorização",
            "Desconto por item segue o limite do usuário. Desconto no total da venda só com autorização do dono (senha do dono no próprio caixa).\n" +
            "Promoções e cupons cadastrados em Promoções são aplicados automaticamente."),
        new("Nota fiscal operando no CPF",
            "Sem CNPJ não é possível emitir NFC-e/NF-e: a área Fiscal fica desligada e o caixa imprime o comprovante não fiscal.\n" +
            "Quando abrir a empresa (MEI/ME), cadastre o CNPJ, o certificado e o token do emissor em Fiscal e faça os testes em homologação antes de ligar."),
        new("Segurança da conta",
            "Ative a verificação em duas etapas em Minha conta (recomendado para o dono e gerentes).\n" +
            "Esqueceu a senha: na tela de login, \"Esqueci minha senha\" envia um código para o e-mail.\n" +
            "Funcionário perdeu o celular do autenticador: o dono remove a verificação dele em Funcionários."),
        new("Atualizações e suporte",
            "Quando houver versão nova aparece um aviso azul no topo — clique em Atualizar agora (o caixa confere a assinatura do arquivo antes de instalar).\n" +
            "Para suporte, fale com quem vendeu o sistema. O suporte só acessa os dados da sua loja depois que você aprova em Suporte."),
    ];

    protected override Task LoadCoreAsync() => Task.CompletedTask;
}
