; Spotlight AI setup wizard (NSIS 3).
; Build after `dotnet publish`:  makensis /DVERSION=1.2.0 installer\SpotlightAI.nsi
; Output: publish\SpotlightAISetup.exe
;
; Installs for all users into Program Files. Settings and sign-ins stay per user in %LOCALAPPDATA%\SpotlightAI.
; Silent install (used by Software Update): SpotlightAISetup.exe /S

Unicode true
ManifestDPIAware true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef SOURCE
  !define SOURCE "..\publish\SpotlightAI.exe"
!endif
!ifndef OUTFILE
  !define OUTFILE "..\publish\SpotlightAISetup.exe"
!endif

!define APP "Spotlight AI"
!define EXE "SpotlightAI.exe"
!define UNINSTALLER "Uninstall Spotlight AI.exe"
!define RUNVALUE "SpotlightAI"

; Test builds install per user under other names, so they never touch a real installation.
!ifdef TEST
  !define ROOT "HKCU"
  !define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SpotlightAITest"
  !define LINK "Spotlight AI Test"
  !undef RUNVALUE
  !define RUNVALUE "SpotlightAITest"
  RequestExecutionLevel user
!else
  !define ROOT "HKLM"
  !define KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SpotlightAI"
  !define LINK "Spotlight AI"
  RequestExecutionLevel admin
!endif
; Versions 1.0 and 1.1 installed themselves per user, without this wizard.
!ifndef OLDDIR
  !define OLDDIR "$LOCALAPPDATA\Programs\SpotlightAI"
!endif
!ifndef OLDKEY
  !define OLDKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SpotlightAI"
!endif

Name "${APP}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\Spotlight AI"
InstallDirRegKey ${ROOT} "${KEY}" "InstallLocation"
SetCompressor /SOLID lzma
BrandingText " "

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=0 "ProductName" "${APP}"
VIAddVersionKey /LANG=0 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=0 "FileDescription" "${APP} Setup"
VIAddVersionKey /LANG=0 "LegalCopyright" "Copyright (c) 2026 mondieuu"

!define MUI_ICON "..\Assets\odak.ico"
!define MUI_UNICON "..\Assets\odak.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION StartFromFinish
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; The wizard follows the Windows display language.
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Turkish"

LangString SecDesktop ${LANG_ENGLISH} "Desktop shortcut"
LangString SecDesktop ${LANG_TURKISH} "Masaüstü kısayolu"
LangString SecStartup ${LANG_ENGLISH} "Start with Windows"
LangString SecStartup ${LANG_TURKISH} "Windows ile başlat"
LangString AskData ${LANG_ENGLISH} "Also delete your Spotlight AI settings and sign-ins on this PC?$\r$\n$\r$\nYour chats stay in your ChatGPT and Gemini accounts."
LangString AskData ${LANG_TURKISH} "Bu bilgisayardaki Spotlight AI ayarların ve oturumların da silinsin mi?$\r$\n$\r$\nSohbetlerin ChatGPT ve Gemini hesaplarında kalır."


Function .onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

Function un.onInit
  ${If} ${RunningX64}
    SetRegView 64
  ${EndIf}
FunctionEnd

; Starts the app as the signed-in user, not as administrator: Explorer opens a shortcut made for it.
; $R9 holds its arguments.
Function StartApp
  StrCpy $0 "$PLUGINSDIR\start.lnk"
  CreateShortcut "$0" "$INSTDIR\${EXE}" "$R9"
  Exec '"$WINDIR\explorer.exe" "$0"'
FunctionEnd

Function StartFromFinish
  StrCpy $R9 ""
  Call StartApp
FunctionEnd

!macro CloseApp
  ; Only this user's copy is closed, so the files can be replaced.
  nsExec::Exec 'cmd.exe /c taskkill /F /IM "${EXE}" /FI "USERNAME eq %USERNAME%"'
  Pop $0
  Sleep 800
!macroend

Section "-Spotlight AI" SecCore
  SectionIn RO
  InitPluginsDir
!ifndef TEST
  !insertmacro CloseApp
!endif

  ; Move over from versions 1.0 and 1.1: their program folder and Apps entry go, settings and sign-ins stay.
  SetShellVarContext current
  ${If} ${FileExists} "${OLDDIR}\${EXE}"
!ifndef TEST
    Delete "$DESKTOP\Spotlight AI.lnk"
    Delete "$SMPROGRAMS\Spotlight AI.lnk"
!endif
    RMDir /r "${OLDDIR}"
    DeleteRegKey HKCU "${OLDKEY}"
  ${EndIf}

  SetOutPath "$INSTDIR"
  File "/oname=${EXE}" "${SOURCE}"
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"

!ifdef TEST
  SetShellVarContext current
!else
  SetShellVarContext all
!endif
  CreateShortcut "$SMPROGRAMS\${LINK}.lnk" "$INSTDIR\${EXE}" "--open"

  WriteRegStr ${ROOT} "${KEY}" "DisplayName" "${APP}"
  WriteRegStr ${ROOT} "${KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr ${ROOT} "${KEY}" "Publisher" "mondieuu"
  WriteRegStr ${ROOT} "${KEY}" "DisplayIcon" '"$INSTDIR\${EXE}",0'
  WriteRegStr ${ROOT} "${KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr ${ROOT} "${KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr ${ROOT} "${KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
  WriteRegStr ${ROOT} "${KEY}" "URLInfoAbout" "https://github.com/mondieuu/spotlight-ai"
  WriteRegDWORD ${ROOT} "${KEY}" "NoModify" 1
  WriteRegDWORD ${ROOT} "${KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD ${ROOT} "${KEY}" "EstimatedSize" "$0"
SectionEnd

Section "$(SecDesktop)" SecDesktop
!ifdef TEST
  SetShellVarContext current
!else
  SetShellVarContext all
!endif
  CreateShortcut "$DESKTOP\${LINK}.lnk" "$INSTDIR\${EXE}" "--open"
SectionEnd

Section "$(SecStartup)" SecStartup
  ; Starts with the signed-in user's Windows session, in the background.
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${RUNVALUE}" '"$INSTDIR\${EXE}" --tray'
SectionEnd

Section "-Finish"
  ${IfNot} ${SectionIsSelected} ${SecStartup}
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${RUNVALUE}"
  ${EndIf}
!ifndef TEST
  ; A silent install is an update: start the new version again in the background.
  ${If} ${Silent}
    StrCpy $R9 "--tray --updated"
    Call StartApp
  ${EndIf}
!endif
SectionEnd

Section "Uninstall"
!ifndef TEST
  !insertmacro CloseApp
!endif
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\${UNINSTALLER}"
  RMDir "$INSTDIR"

!ifdef TEST
  SetShellVarContext current
!else
  SetShellVarContext all
!endif
  Delete "$SMPROGRAMS\${LINK}.lnk"
  Delete "$DESKTOP\${LINK}.lnk"
  DeleteRegKey ${ROOT} "${KEY}"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${RUNVALUE}"

  ; Settings and sign-ins are kept unless the user asks to remove them (never in a silent uninstall).
  SetShellVarContext current
  MessageBox MB_YESNO|MB_ICONQUESTION "$(AskData)" /SD IDNO IDNO keep
    RMDir /r "$LOCALAPPDATA\SpotlightAI"
    RMDir /r "$TEMP\.net\SpotlightAI"
  keep:
SectionEnd
