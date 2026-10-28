# App do proprietário (Flutter — Android)

Compilado e testado em 25/10/2026: Flutter 3.47.8, `flutter analyze` sem problemas, `flutter test` aprovado,
APK de produção assinado. Ferramentas instaladas em `E:\dev-tools` (Flutter, JDK 17, Android SDK).

## Gerar e atualizar (um comando)
```
powershell -ExecutionPolicy Bypass -File dev\build-apk.ps1 -Publish -Notes "O que mudou"
```
- Sobe a versão no `pubspec.yaml` (1.0.1+2 → 1.0.2+3), gera o APK assinado e copia para `artifacts\`.
- `-Publish`: coloca o APK para download no servidor (`/downloads/...`) e publica a versão no painel
  (Atualizações → App Android). Ao abrir, os celulares com versão menor mostram **"Nova versão — Atualizar"**.
- Padrão do app: IP deste computador na rede local, porta 5080. Para produção: `-ApiUrl https://api.seudominio.com.br`.

**Chave de assinatura**: `mobile/android/upload-keystore.jks` + `mobile/android/key.properties` (fora do Git).
Faça backup: o Android só instala uma versão por cima da outra se a chave for a mesma.

## Testar no celular pela rede local
1. Computador e celular no mesmo roteador/Wi-Fi. API aberta (`dev\run-api.ps1` já escuta em `0.0.0.0:5080`).
2. Firewall do Windows liberando a porta 5080 para a rede local (uma vez, PowerShell **como administrador**):
   `New-NetFirewallRule -DisplayName "Comercial API (rede local)" -Direction Inbound -Protocol TCP -LocalPort 5080 -RemoteAddress LocalSubnet -Action Allow`
3. No celular, abra no navegador `http://IP-DO-COMPUTADOR:5080/downloads/comercial-X.Y.Z.apk` e instale
   (o Android pede para permitir "instalar apps desta fonte").
4. Se o IP do computador mudar: tela de login → "Servidor" → novo endereço → "Salvar e testar conexão".

## O que existe
- Login (código da empresa, e-mail, senha e código do autenticador quando a 2FA estiver ligada); refresh token no Keystore.
- Painel: vendas do dia/mês, caixas abertos, estoque baixo, licença, última sincronização.
- Estoque: lista com fotos, entrada de mercadoria e cadastro de produto pela câmera.
- Avisos: quebra de caixa e outros avisos.
- Botão do WhatsApp de atendimento (número do painel admin) e aviso de atualização.
- HTTP só é aceito na rede local (IPs privados); fora dela o app exige HTTPS.

## Pendências
- Push (Firebase): precisa do `google-services.json` do seu projeto Firebase; sem ele os avisos ficam só na aba Avisos.
- iOS não gerado (exige Mac).
- Emulador não testado neste PC (virtualização do Windows desligada); validado por testes, análise e chamadas reais pela rede.
