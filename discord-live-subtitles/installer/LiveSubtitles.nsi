; NSIS installer for Live Subtitles for Discord.
; Builds on Windows or Linux:  makensis -DVERSION=0.2.0 installer/LiveSubtitles.nsi
; (expects the self-contained publish output in artifacts/publish; see installer/build-installer-nsis.sh)
; Per-user install (no admin rights). The .NET runtime and both ONNX models are included.

Unicode true
!ifndef VERSION
  !define VERSION "0.2.0"
!endif
!define APPNAME "Live Subtitles for Discord"
!define EXE "LiveSubtitles.exe"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveSubtitles"
!define PUBLISH "..\artifacts\publish"

Name "${APPNAME}"
OutFile "Output\LiveSubtitlesSetup-${VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\LiveSubtitles"
InstallDirRegKey HKCU "${UNINSTKEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
ManifestDPIAware true
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "LegalCopyright" "Live Subtitles"

!include "MUI2.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

!define MUI_ICON "..\src\LiveSubtitles.App\Assets\app.ico"
!define MUI_UNICON "..\src\LiveSubtitles.App\Assets\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "This installs ${APPNAME} ${VERSION}: live English subtitles for Discord voice calls, using OpenAI's Realtime Translation or Soniox.$\r$\n$\r$\nEverything runs on this PC. You will need your own OpenAI or Soniox API key (the read-me explains how to get one).$\r$\n$\r$\nNo administrator rights are needed."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME} now"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\README.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the read-me (setup, API key, testing)"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APPNAME} needs 64-bit Windows 10 or 11."
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
  ${OrIfNot} ${AtLeastBuild} 19041
    MessageBox MB_ICONSTOP "${APPNAME} needs Windows 10 version 2004 (build 19041) or newer, for per-app audio capture."
    Abort
  ${EndIf}
FunctionEnd

; Ask the user to close a running copy (files in use can't be replaced).
!macro CloseRunningApp
  retry:
  nsExec::ExecToStack 'cmd /c tasklist /FI "IMAGENAME eq ${EXE}" /NH | find /I "${EXE}"'
  Pop $0
  Pop $1
  ${If} $0 == 0
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${APPNAME} is running. Please exit it (right-click the tray icon → Exit), then click Retry." IDRETRY retry
    Abort
  ${EndIf}
!macroend

Section "${APPNAME} (required)" SecApp
  SectionIn RO
  !insertmacro CloseRunningApp
  SetOutPath "$INSTDIR"
  RMDir /r "$INSTDIR\models"
  File /r "${PUBLISH}\*.*"
  File "..\README.md"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXE}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Read me.lnk" "$INSTDIR\README.md"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTKEY}" "Publisher" "Live Subtitles"
  WriteRegStr HKCU "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTKEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTKEY}" "EstimatedSize" 285000
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXE}"
SectionEnd

Section /o "Start when I sign in to Windows (in the tray)" SecAutostart
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "LiveSubtitles" '"$INSTDIR\${EXE}"'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "The app, the .NET runtime and the local speech and speaker models (about 280 MB)."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Put a shortcut on the desktop."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} "Start minimised in the notification area when you sign in."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  !insertmacro CloseRunningApp
  RMDir /r "$INSTDIR"
  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "LiveSubtitles"
  DeleteRegKey HKCU "${UNINSTKEY}"
  IfSilent done
  MessageBox MB_YESNO|MB_ICONQUESTION "Also delete your settings, remembered voice profiles, logs and the saved API keys?$\r$\n$\r$\n(Transcripts and test audio you saved yourself are not touched.)" IDNO done
    RMDir /r "$APPDATA\LiveSubtitles"
    nsExec::Exec 'cmdkey /delete:LiveSubtitles/OpenAI-API-Key'
    nsExec::Exec 'cmdkey /delete:LiveSubtitles/Soniox-API-Key'
  done:
SectionEnd
