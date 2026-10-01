; Notch installer (NSIS 3). Installs per user, so it never asks for admin rights.
;
; Build the app first (see .github/workflows/release.yml), then:
;   makensis /DVERSION=0.1.0 /DFILE_VERSION=0.1.0.0 installer\Notch.nsi
;
; Switches: /S installs silently. /UPDATE (used by the app's updater, with /S) leaves the
; start-with-Windows setting alone and restarts the app when the install is done.
;
; Optional defines: SOURCE_DIR (published app folder), OUTPUT_FILE (installer path).
; Relative paths are resolved from this script's folder.

Unicode true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "LogicLib.nsh"

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
; Four plain numbers for the installer's file properties (VERSION may carry a suffix like -beta).
!ifndef FILE_VERSION
  !define FILE_VERSION "0.0.0.0"
!endif
!ifndef SOURCE_DIR
  !define SOURCE_DIR "..\artifacts\publish"
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "..\artifacts\Notch-Setup-${VERSION}.exe"
!endif

!define APP_NAME "Notch"
!define APP_EXE "Notch.exe"
!define APP_KEY "Software\Notch"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Notch"
!define RUN_KEY "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallDir"
RequestExecutionLevel user
ShowInstDetails nevershow
ShowUninstDetails nevershow

VIProductVersion "${FILE_VERSION}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APP_NAME} installer"
VIAddVersionKey "LegalCopyright" "Notch contributors"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME}"

!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; 1 when started by the app's updater (/UPDATE).
Var IsUpdate

; A running copy keeps its files locked, so stop it before touching them.
!macro StopRunningApp
  nsExec::Exec 'taskkill /IM "${APP_EXE}" /F'
  Pop $0
  Sleep 800
!macroend

Section "${APP_NAME}" SecApp
  SectionIn RO
  !insertmacro StopRunningApp

  ; Clear the previous version so files it no longer ships do not linger.
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    RMDir /r "$INSTDIR"
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateShortcut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKCU "${APP_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Notch contributors"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" "$0"
SectionEnd

; The same Run value the app's own "Start with Windows" setting manages.
Section "Start with Windows" SecStartup
  WriteRegStr HKCU "${RUN_KEY}" "${APP_NAME}" '"$INSTDIR\${APP_EXE}"'
SectionEnd

; The app's updater runs the installer as "/S /UPDATE"; put the app back on screen when done.
Section "-Restart after update"
  ${If} $IsUpdate == 1
    Exec '"$INSTDIR\${APP_EXE}" --updated'
  ${EndIf}
SectionEnd

Function .onInit
  StrCpy $IsUpdate 0
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/UPDATE" $1
  ${IfNot} ${Errors}
    StrCpy $IsUpdate 1
    ; An update must not override whether the user wants Notch to start with Windows.
    SectionSetFlags ${SecStartup} 0
  ${EndIf}
FunctionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "The Notch app."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartup} "Start Notch automatically when you sign in."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; Settings in %AppData%\Notch are left in place so a reinstall keeps them.
Section "Uninstall"
  !insertmacro StopRunningApp

  ; Only remove the folder if it really is a Notch install.
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    RMDir /r "$INSTDIR"
  ${EndIf}

  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  DeleteRegValue HKCU "${RUN_KEY}" "${APP_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"
SectionEnd
