; Inno Setup 6 script for Live Subtitles for Discord.
; Built by installer/build-installer.ps1 (locally) or the GitHub Actions workflow.
; Installs per user (no admin rights needed); the .NET runtime and both ONNX models are bundled.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define MyAppName "Live Subtitles for Discord"
#define MyAppExe "LiveSubtitles.exe"

[Setup]
AppId={{6F0D2C4E-8E0B-4E7B-9C5E-3B1B8C5A7D21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=LiveSubtitles
DefaultDirName={localappdata}\Programs\LiveSubtitles
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=LiveSubtitlesSetup-{#MyAppVersion}
SetupIconFile=..\src\LiveSubtitles.App\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 (build 19041) is needed for per-app audio capture.
MinVersion=10.0.19041
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Live Subtitles when I sign in to Windows (minimised to the tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Read me"; Filename: "{app}\README.md"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "LiveSubtitles"; \
  ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete your settings, remembered voice profiles, logs and the saved OpenAI API key?' + #13#10 +
              '(Transcripts and test audio you saved yourself are not touched.)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\LiveSubtitles'), True, True, True);
      Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:LiveSubtitles/OpenAI-API-Key', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
