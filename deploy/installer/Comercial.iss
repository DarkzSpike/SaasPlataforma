; Instalador do caixa (PDV) - Inno Setup 6 (https://jrsoftware.org/isinfo.php, gratuito).
; Gerado por dev/publish-pdv.ps1, que passa /DAppVersion=x.y.z e /DSourceDir=<pasta publicada>.
; Os dados da loja (banco local criptografado, chave, sessão) ficam em %LOCALAPPDATA% e NÃO são apagados
; na atualização nem na desinstalação: vendas offline nunca se perdem por reinstalar.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\pdv"
#endif

[Setup]
AppId={{6F1C2B7E-4E0B-4F5B-9B1D-3C2A7A9E5D10}
AppName=Sistema Comercial - Caixa
AppVersion={#AppVersion}
AppPublisher=Sistema Comercial
DefaultDirName={autopf}\Sistema Comercial
DefaultGroupName=Sistema Comercial
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts
OutputBaseFilename=Comercial-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
UninstallDisplayIcon={app}\Comercial.Desktop.exe

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Sistema Comercial - Caixa"; Filename: "{app}\Comercial.Desktop.exe"
Name: "{autodesktop}\Sistema Comercial - Caixa"; Filename: "{app}\Comercial.Desktop.exe"; Tasks: desktopicon

[Run]
; Também roda na atualização silenciosa (o caixa fecha, instala e abre de novo).
Filename: "{app}\Comercial.Desktop.exe"; Description: "Abrir o caixa agora"; Flags: nowait postinstall
