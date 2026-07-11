; Glimpse installer.
;
; Shape: per-user (no admin prompt), self-contained payload (no .NET install),
; WebView2 bootstrapped only when the machine genuinely lacks it. The app never
; registers file associations, so there is nothing to negotiate with the user's
; default apps. Uninstall quits the resident process, removes the sign-in
; entry, and deletes the app's data folder.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppName "Glimpse"
#define MyAppExeName "Glimpse.exe"
#define MyOldAppExeName "SpacePreview.exe"
#define MyMutexName "Local\SpacePreview.Instance"

[Setup]
AppId={{7C3A9F41-6E8B-4D2C-9A57-D14B20F3E6A8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=Glimpse-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\Glimpse.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when you sign in"
; Offered only when the machine has no compatible document engine at all:
; downloads LibreOffice and installs it purely as an engine (no shortcuts,
; no file-association changes).
Name: "fullpreviews"; Description: "Full Word, Excel and PowerPoint previews (LibreOffice, 355 MB)"; Check: OfficeEngineMissing

[Files]
Source: "..\artifacts\publish\win-x64-standalone\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "assets\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: WebView2Missing

[InstallDelete]
Type: filesandordirs; Name: "{localappdata}\Programs\SpacePreview"
Type: files; Name: "{app}\SpacePreview.exe"
Type: files; Name: "{app}\SpacePreview.dll"
Type: files; Name: "{app}\SpacePreview.deps.json"
Type: files; Name: "{app}\SpacePreview.runtimeconfig.json"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Setting up the document viewer..."; Check: WebView2Missing; Flags: waituntilterminated
; Engine only: no desktop link, no Quickstarter, and it never takes over
; anyone's file associations. Elevation is msiexec's own UAC prompt.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\LibreOffice.msi"" /qn CREATEDESKTOPLINK=0 QUICKSTART=0 REGISTER_ALL_MSO_TYPES=0 REGISTER_NO_MSO_TYPES=1 ISCHECKFORPRODUCTUPDATES=0 REBOOTYESNO=No"; StatusMsg: "Setting up full document previews (a few minutes)..."; Check: EngineDownloadSucceeded; Verb: "runas"; Flags: shellexec waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Parameters: "--install-startup"; Tasks: startup; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Glimpse"
Type: filesandordirs; Name: "{localappdata}\SpacePreview"

#define LibreOfficeUrl "https://downloadarchive.documentfoundation.org/libreoffice/old/26.2.4.2/win/x86_64/LibreOffice_26.2.4.2_Win_x86-64.msi"
#define LibreOfficeSha256 "202f26cda071c5aa4996a5a28412fddceb3891dceb0366982c62650456c0730f"

[Code]
var
  EngineDownloadPage: TDownloadWizardPage;
  EngineDownloadOk: Boolean;

// Only engines the app's converter can actually drive count as present.
function HasOfficeEngineRegistry(const RootKey: Integer; const SubKey: String): Boolean;
var
  InstallPath: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, '', InstallPath)
    and FileExists(AddBackslash(InstallPath) + 'soffice.exe');
end;

function OfficeEngineMissing(): Boolean;
begin
  Result := not (
    FileExists(ExpandConstant('{commonpf64}\LibreOffice\program\soffice.exe'))
    or FileExists(ExpandConstant('{commonpf32}\LibreOffice\program\soffice.exe'))
    or FileExists(ExpandConstant('{localappdata}\Programs\LibreOffice\program\soffice.exe'))
    or FileExists(ExpandConstant('{commonpf64}\OpenOffice 4\program\soffice.exe'))
    or FileExists(ExpandConstant('{commonpf32}\OpenOffice 4\program\soffice.exe'))
    or FileExists(ExpandConstant('{localappdata}\Programs\OpenOffice 4\program\soffice.exe'))
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\LibreOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKCU, 'SOFTWARE\LibreOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\OpenOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\WOW6432Node\OpenOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKCU, 'SOFTWARE\OpenOffice\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\OpenOffice.org\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKLM, 'SOFTWARE\WOW6432Node\OpenOffice.org\UNO\InstallPath')
    or HasOfficeEngineRegistry(HKCU, 'SOFTWARE\OpenOffice.org\UNO\InstallPath'));
end;

function EngineDownloadSucceeded(): Boolean;
begin
  Result := EngineDownloadOk;
end;

procedure InitializeWizard();
begin
  EngineDownloadPage := CreateDownloadPage(
    'Full document previews',
    'Getting LibreOffice from The Document Foundation...',
    nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('fullpreviews') then
  begin
    EngineDownloadPage.Clear;
    EngineDownloadPage.Add('{#LibreOfficeUrl}', 'LibreOffice.msi', '{#LibreOfficeSha256}');
    EngineDownloadPage.Show;
    try
      try
        EngineDownloadPage.Download;
        EngineDownloadOk := True;
      except
        // Offline or cancelled: the app still installs and works, documents
        // preview in their basic form, and the in-app card offers the fix.
        EngineDownloadOk := False;
        if EngineDownloadPage.AbortedByUser then
          Log('Engine download cancelled; continuing without it.')
        else
          SuppressibleMsgBox(
            'The LibreOffice download did not finish. Glimpse will still work; documents show a basic preview until LibreOffice is installed.',
            mbInformation, MB_OK, IDOK);
      end;
    finally
      EngineDownloadPage.Hide;
    end;
  end;
end;

function HasWebView2Key(const RootKey: Integer; const SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version)
    and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Missing(): Boolean;
begin
  Result := not (
    HasWebView2Key(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
    or HasWebView2Key(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')
    or HasWebView2Key(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'));
end;

// Ask a resident Glimpse to leave, then wait for its mutex to vanish.
// Falls back to taskkill for a copy running from somewhere else (portable,
// dev build) that the installed exe's quit signal has already handled anyway.
procedure QuitResident(const ExePath: String);
var
  ResultCode: Integer;
  Tries: Integer;
begin
  if not CheckForMutexes('{#MyMutexName}') then
    Exit;

  if FileExists(ExePath) then
    Exec(ExePath, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Tries := 0;
  while CheckForMutexes('{#MyMutexName}') and (Tries < 20) do
  begin
    Sleep(250);
    Tries := Tries + 1;
  end;

  if CheckForMutexes('{#MyMutexName}') then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyOldAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitResident(ExpandConstant('{app}\{#MyAppExeName}'));
  QuitResident(ExpandConstant('{app}\{#MyOldAppExeName}'));
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    QuitResident(ExpandConstant('{app}\{#MyAppExeName}'));
    if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--uninstall-startup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      Exec(ExpandConstant('{app}\{#MyOldAppExeName}'), '--uninstall-startup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
