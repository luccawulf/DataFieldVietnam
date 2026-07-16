; AppExeName is the exe filename (DataFieldVietnam.exe) -- the in-game hook launches the client by that
; literal and the updater replaces a file by that name. AppId is the Inno uninstall identity and MUST be
; distinct from upstream's BF1942 DataField42 (which also used "DataField42"): sharing it makes this
; installer find that tool's uninstall key and refuse as an already-installed upgrade. AppDisplayName is
; the human-facing name. This installer targets Battlefield Vietnam: it validates BfVietnam.exe and
; defaults to a BFV folder.
#define AppId "DataFieldVietnam"
#define AppExeName "DataFieldVietnam"
#define AppDisplayName "DataField Vietnam"
#define AppExePath "..\DataField42\bin\Publish\" + AppExeName + ".exe"
#define AppVersion GetFileVersion(AppExePath)

[Setup]
AppId={#AppId}
AppName={#AppDisplayName}
UninstallDisplayName={#AppDisplayName}
AppVersion={#AppVersion}
WizardStyle=modern
ShowLanguageDialog=auto
DefaultDirName={code:GetBfVietnamDirectory}
DirExistsWarning=no
AppendDefaultDirName=no
DefaultGroupName={code:GetBfVietnamGroup}
DisableProgramGroupPage=yes
DisableReadyPage=yes
SolidCompression=yes
Compression=lzma2/ultra
SetupIconFile=../DataField42/logo.ico
UninstallDisplayIcon={app}\{#AppExeName}.exe
UninstallFilesDir={app}\{#AppId}
OutputDir=bin
OutputBaseFilename={#AppDisplayName} v{#AppVersion} Installer

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "sp"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[Messages]
WizardSelectDir=Select the Battlefield Vietnam installation folder
sp.WizardSelectDir=Selecciona la carpeta de instalación de Battlefield Vietnam
fr.WizardSelectDir=Sélectionnez le dossier d'installation de Battlefield Vietnam
it.WizardSelectDir=Seleziona la cartella di installazione di Battlefield Vietnam
de.WizardSelectDir=Wählen Sie den Installationsordner von Battlefield Vietnam aus
ru.WizardSelectDir=Выберите папку установки Battlefield Vietnam
ja.WizardSelectDir=Battlefield Vietnam のインストールフォルダを選択してください

SelectDirLabel3=Setup will install [name] into the following folder. This must be the Battlefield Vietnam installation folder.
sp.SelectDirLabel3=La instalación colocará [name] en la siguiente carpeta. Esta debe ser la carpeta de instalación de Battlefield Vietnam.
fr.SelectDirLabel3=L'installation placera [name] dans le dossier suivant. Ceci doit être le dossier d'installation de Battlefield Vietnam.
it.SelectDirLabel3=L'installazione installerà [name] nella seguente cartella. Questa deve essere la cartella di installazione di Battlefield Vietnam.
de.SelectDirLabel3=Die Installation wird [name] in den folgenden Ordner installieren. Dies muss der Installationsordner von Battlefield Vietnam sein.
ru.SelectDirLabel3=Установка разместит [name] в следующей папке. Это должна быть папка установки Battlefield Vietnam.
ja.SelectDirLabel3=Setupは[name]を次のフォルダにインストールします。これはBattlefield Vietnamのインストールフォルダである必要があります。

[CustomMessages]
en.NotValidBfVietnamDirectory=Please select a valid Battlefield Vietnam directory!
sp.NotValidBfVietnamDirectory=¡Por favor, selecciona un directorio de Battlefield Vietnam válido!
fr.NotValidBfVietnamDirectory=Veuillez sélectionner un répertoire Battlefield Vietnam valide !
it.NotValidBfVietnamDirectory=Si prega di selezionare una directory Battlefield Vietnam valida!
de.NotValidBfVietnamDirectory=Bitte wählen Sie ein gültiges Battlefield-Vietnam-Verzeichnis aus!
ru.NotValidBfVietnamDirectory=Выберите действительный каталог Battlefield Vietnam, пожалуйста!
ja.NotValidBfVietnamDirectory=有効なBattlefield Vietnamディレクトリを選択してください！

en.Run=Run %1
sp.Run=Ejecutar %1
fr.Run=Lancer %1
it.Run=Esegui %1
de.Run=%1 starten
ru.Run=Запустить %1
ja.Run=%1を実行

[Files]
Source: {#AppExePath}; DestDir: {app}

[Icons]
Name: {commondesktop}\{#AppDisplayName}; Filename: {app}\{#AppExeName}.exe; WorkingDir: {app}
Name: {group}\{#AppDisplayName}; Filename: {app}\{#AppExeName}.exe; WorkingDir: {app}

[Run]
Filename: "{app}\{#AppExeName}.exe"; Parameters: "install";
Filename: "{app}\{#AppExeName}.exe"; Description: {cm:Run,{#AppDisplayName}}; Flags: nowait postinstall

[Code]
function CheckBfVietnamDirectory(DirectoryPath: String): Boolean;
begin
  Result := FileExists(DirectoryPath + '\BfVietnam.exe');
end;


function GetBfVietnamDirectory(def: String): String;
var
  PathFromRegistry : string;
begin
  Result := ''; // No guess -- the user browses, and NextButtonClick validates BfVietnam.exe.

  // EA/Origin do not reliably record a game directory for Battlefield Vietnam (the registry key holds
  // only the CD-key), so these reads are best-effort and usually fall through to the folder checks.
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Electronic Arts\EA Games\Battlefield Vietnam', 'Install Dir', PathFromRegistry) and CheckBfVietnamDirectory(PathFromRegistry) then
    Result := PathFromRegistry
  else if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\EA GAMES\Battlefield Vietnam', 'GAMEDIR', PathFromRegistry) and CheckBfVietnamDirectory(PathFromRegistry) then
    Result := PathFromRegistry
  else if CheckBfVietnamDirectory(ExpandConstant('{pf32}') + '\EA GAMES\Battlefield Vietnam') then
    Result := ExpandConstant('{pf32}') + '\EA GAMES\Battlefield Vietnam'
  else if CheckBfVietnamDirectory(ExpandConstant('{pf32}') + '\Origin Games\Battlefield Vietnam') then
    Result := ExpandConstant('{pf32}') + '\Origin Games\Battlefield Vietnam'
  else if CheckBfVietnamDirectory(ExpandConstant('{pf}') + '\EA GAMES\Battlefield Vietnam') then
    Result := ExpandConstant('{pf}') + '\EA GAMES\Battlefield Vietnam';
end;


function GetBfVietnamGroup(def: String): String;
begin
  Result := 'EA GAMES\Battlefield Vietnam';

  if DirExists(ExpandConstant('{userprograms}') + '\EA GAMES\Battlefield Vietnam') then
    Result := 'EA GAMES\Battlefield Vietnam'
  else if DirExists(ExpandConstant('{userprograms}') + '\Battlefield Vietnam') then
    Result := 'Battlefield Vietnam'
end;


function GetIsUpgrade: Boolean;
var
  SetupReg: string;
begin
  SetupReg := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#AppId}_is1';
  Result := RegKeyExists(HKEY_LOCAL_MACHINE, SetupReg) or RegKeyExists(HKEY_CURRENT_USER, SetupReg);
end;

(* Event Functions: *)

function InitializeSetup(): Boolean;
begin
  Result := True;
  if GetIsUpgrade() then
    begin
      MsgBox('{#AppDisplayName} is already installed.', mbInformation, MB_OK);
      Result := False;
    end;
end;


function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True
  if (CurPageID = wpSelectDir) and (not CheckBfVietnamDirectory(WizardDirValue)) then begin
    MsgBox(ExpandConstant('{cm:NotValidBfVietnamDirectory}'), mbError, MB_OK)
    Result := False;
  end;
end;


procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
end;
