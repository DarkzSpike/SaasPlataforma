using System.Runtime.CompilerServices;
using Comercial.Desktop.Core;

namespace Comercial.Tests;

/// <summary>
/// O motor SQLite é global no processo. Os testes usam a API (SQLite comum) e o banco do caixa (SQLCipher) ao mesmo
/// tempo; trocar o motor no meio da execução quebra conexões já abertas. Por isso o SQLCipher é ligado uma única vez,
/// antes de qualquer teste (sem chave ele se comporta como SQLite comum).
/// </summary>
internal static class SqliteProviderSetup
{
    [ModuleInitializer]
    internal static void Init() => LocalDatabase.EnsureCipherProvider();
}
