using Comercial.Desktop.Core;
using Xunit;

namespace Comercial.Tests;

/// <summary>Multiplicador de quantidade digitado na busca do PDV.</summary>
public class QuantityInputTests
{
    [Theory]
    [InlineData("3*caderno", 3, "caderno")]
    [InlineData("3 * caderno universitário", 3, "caderno universitário")]
    [InlineData("*3 caderno", 3, "caderno")]
    [InlineData("* 3 caderno", 3, "caderno")]
    [InlineData("caderno*3", 3, "caderno")]
    [InlineData("3x caderno", 3, "caderno")]
    [InlineData("3 X caderno", 3, "caderno")]
    [InlineData("12*7891234567895", 12, "7891234567895")]
    [InlineData("1,5*arroz", 1.5, "arroz")]
    public void Separa_quantidade_e_produto(string input, double qty, string term)
    {
        Assert.True(QuantityInput.TryParse(input, out var q, out var t, out var err), err);
        Assert.Equal((decimal)qty, q);
        Assert.Equal(term, t);
    }

    [Theory]
    [InlineData("caderno")]
    [InlineData("Caixa 20 x 30")]           // nome com "x" não é multiplicador
    [InlineData("7891234567895")]
    public void Sem_multiplicador_e_uma_unidade(string input)
    {
        Assert.True(QuantityInput.TryParse(input, out var q, out var t, out _));
        Assert.Equal(1m, q);
        Assert.Equal(input, t);
    }

    [Theory]
    [InlineData("0*caderno")]
    [InlineData("10000*caderno")]
    [InlineData("3*")]
    [InlineData("*caderno")]
    public void Quantidade_invalida_e_recusada_com_mensagem(string input)
    {
        Assert.False(QuantityInput.TryParse(input, out _, out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }
}
