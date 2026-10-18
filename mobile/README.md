# App do proprietário (Flutter — Android e iOS)

> **Status: código-fonte NÃO compilado.** O Flutter SDK não estava instalado no ambiente em que este
> código foi escrito. Antes de usar: instale o Flutter estável, rode `flutter create . --platforms=android,ios`
> nesta pasta (gera as pastas nativas sem sobrescrever `lib/`), depois `flutter analyze` e `flutter test`.

## Executar
```
flutter pub get
flutter run --dart-define=API_BASE_URL=https://api.seudominio.com.br
```
No emulador Android, a API local de desenvolvimento fica em `http://10.0.2.2:5080` (padrão).

## O que existe
- Login com código da empresa, e-mail e senha; refresh token no armazenamento seguro (Keychain/Keystore).
- Dashboard (`/api/v1/mobile/dashboard`): vendas do dia/mês, caixas abertos, estoque baixo, licença,
  última sincronização — com aviso de dados desatualizados.
- Identidade visual dinâmica por empresa (`/api/v1/branding`), com tema claro/escuro; a identidade é
  limpa no logout/troca de conta para não vazar entre empresas.
- Todas as regras (módulo `app_proprietario`, permissão `reports.read`, licença) são validadas no servidor.

## Pendências
- Nome e ícone nativos do app são do produto; ícone/nome por cliente exigem builds separados (flavors).
- Telas de estoque, caixa, horários e relatórios detalhados.
- Testes de widget.
