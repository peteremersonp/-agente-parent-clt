; ParentCLT - Instalador del agente de control parental (Windows 10/11)
; Compilacion: iscc installer.iss  (tras ejecutar scripts\publish.ps1)

#define MyAppName "ParentCLT"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ParentCLT"
#define MyAppExeName "ParentCLT.Agent.exe"
#define ServiceName "ParentCLT Agent"
#define WatchdogTask "ParentCLT Watchdog"

[Setup]
AppId={{8C9E4F2D-6A3B-4E5F-9A1C-2D3E4F5A6B7C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=ParentCLT-Setup
SetupLogging=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Empaqueta el publish self-contained COMPLETO (runtime .NET incluido) para que
; el agente funcione en maquinas limpias sin .NET instalado.
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "watchdog.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "appsettings.template.json"; DestDir: "{app}"; DestName: "appsettings.json"; Flags: ignoreversion

[UninstallDelete]
Type: dirifempty; Name: "{app}"
Type: filesandordirs; Name: "{commonappdata}\ParentCLT"

[Code]
var
  ServerPage: TInputQueryWizardPage;
  ServerUrl: String;
  DeviceName: String;

const
  Service = '{#ServiceName}';
  WatchdogTaskName = '{#WatchdogTask}';
  DefaultUrl = 'http://localhost:8899';

const
  AppName = '{#MyAppName}';
  ExeName = '{#MyAppExeName}';

function NormalizeServerUrl(Value: String): String;
begin
  Value := Trim(Value);
  if Value = '' then
    Value := DefaultUrl;
  if (Pos('://', Value) = 0) then
    Value := 'https://' + Value;
  while (Length(Value) > 0) and (Value[Length(Value)] = '/') do
    Delete(Value, Length(Value), 1);
  Result := Value;
end;

procedure InitializeWizard;
begin
  ServerPage := CreateInputQueryPage(
    wpWelcome,
    'Configuracion del servidor',
    'Datos del servidor y del dispositivo.',
    'Especifica la URL del servidor ParentCLT y el nombre que identificara este equipo en el panel.');

  ServerPage.Add('URL del servidor:', False);
  ServerPage.Add('Nombre del dispositivo:', False);

  ServerUrl := ExpandConstant('{param:SERVERURL|}');
  ServerPage.Values[0] := NormalizeServerUrl(ServerUrl);
  if ServerPage.Values[0] = '' then
    ServerPage.Values[0] := NormalizeServerUrl(DefaultUrl);

  DeviceName := ExpandConstant('{param:DEVICENAME|}');
  if DeviceName = '' then
    DeviceName := GetComputerNameString;
  ServerPage.Values[1] := DeviceName;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = ServerPage.ID then
  begin
    ServerUrl := NormalizeServerUrl(ServerPage.Values[0]);
    DeviceName := Trim(ServerPage.Values[1]);
    if DeviceName = '' then
      DeviceName := GetComputerNameString;
    ServerPage.Values[0] := ServerUrl;
    ServerPage.Values[1] := DeviceName;
  end;
end;

function ReplaceAll(S, OldSub, NewSub: String): String;
var
  P: Integer;
begin
  if NewSub = OldSub then
  begin
    Result := S;
    Exit;
  end;
  while True do
  begin
    P := Pos(OldSub, S);
    if P = 0 then
      Break;
    S := Copy(S, 1, P - 1) + NewSub + Copy(S, P + Length(OldSub), Length(S) - P - Length(OldSub) + 1);
  end;
  Result := S;
end;

procedure GenerateAppSettings;
var
  FilePath, NewText: String;
  List: TStringList;
begin
  FilePath := ExpandConstant('{app}\appsettings.json');
  if not FileExists(FilePath) then
    RaiseException('No se encontro appsettings.json recien instalado');

  List := TStringList.Create;
  try
    List.LoadFromFile(FilePath);
    NewText := ReplaceAll(List.Text, '__SERVER_URL__', ServerUrl);
    NewText := ReplaceAll(NewText, '__DEVICE_NAME__', DeviceName);
    List.Text := NewText;
    List.SaveToFile(FilePath);
  finally
    List.Free;
  end;
end;

procedure RunExec(Cmd, Args: String);
var
  ResultCode: Integer;
begin
  Exec(Cmd, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure ResetService;
begin
  RunExec('sc.exe', 'stop "' + Service + '"');
  RunExec('sc.exe', 'delete "' + Service + '"');
end;

procedure InstallService;
begin
  ResetService;
  RunExec('sc.exe', 'create "' + Service
    + '" binPath= "\"' + ExpandConstant('{app}\' + ExeName) + '\""'
    + ' start= delayed-auto DisplayName= "' + Service + '"');
  RunExec('sc.exe', 'failure "' + Service
    + '" reset= 86400 actions= restart/5000/restart/10000/restart/30000');
end;

procedure InstallWatchdog;
begin
  RunExec('schtasks.exe', '/Create /F /TN "' + WatchdogTaskName
    + '" /TR "' + ExpandConstant('{app}\watchdog.bat') + '"'
    + ' /SC MINUTE /MO 5 /RU SYSTEM /RL HIGHEST');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    GenerateAppSettings;
    InstallService;
    InstallWatchdog;
    RunExec('sc.exe', 'start "' + Service + '"');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  ExePath: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExePath := ExpandConstant('{app}\' + ExeName);

    RunExec('sc.exe', 'stop "' + Service + '"');
    // Matar el proceso para eliminar carreras: un servicio vivo o re-levantado
    // por el watchdog podria re-aplicar el filtro DESPUES del restore.
    RunExec('taskkill.exe', '/F /IM "' + ExeName + '" /T');
    RunExec('sc.exe', 'delete "' + Service + '"');

    // Restaurar el DNS solo cuando NINGUN proceso del agente puede re-aplicarlo.
    if FileExists(ExePath) then
      Exec(ExePath, 'restore-dns', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    RunExec('schtasks.exe', '/Delete /F /TN "' + WatchdogTaskName + '"');
  end;
end;