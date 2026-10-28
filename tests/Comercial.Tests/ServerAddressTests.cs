using Comercial.Desktop.Core;

namespace Comercial.Tests;

/// <summary>Endereço do servidor digitado no caixa: HTTPS em qualquer lugar, HTTP só na rede local.</summary>
public class ServerAddressTests
{
    [Theory]
    [InlineData("192.168.0.10:5080", "http://192.168.0.10:5080")]
    [InlineData("http://10.0.0.5:5080/", "http://10.0.0.5:5080")]
    [InlineData("abc-def.trycloudflare.com", "https://abc-def.trycloudflare.com")]
    [InlineData("https://api.minhaempresa.com.br", "https://api.minhaempresa.com.br")]
    [InlineData("localhost:5080", "http://localhost:5080")]
    public void Aceita(string input, string expected)
    {
        Assert.True(ServerAddress.TryNormalize(input, out var url, out var error), error);
        Assert.Equal(expected, url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://abc.trycloudflare.com")]   // internet sem HTTPS: senha trafegaria aberta
    [InlineData("http://200.10.20.30:5080")]
    [InlineData("ftp://servidor")]
    [InlineData("https://servidor.com/api?x=1")]
    public void Recusa(string input)
    {
        Assert.False(ServerAddress.TryNormalize(input, out _, out var error));
        Assert.NotNull(error);
    }
}
