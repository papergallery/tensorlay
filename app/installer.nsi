; TensorLay Installer — NSIS + Branded UI
; Native exe, no .NET needed, custom graphics

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "nsDialogs.nsh"

; ─── General ───
Name "TensorLay"
OutFile "TensorLay-Setup.exe"
InstallDir "$PROGRAMFILES64\TensorLay"
InstallDirRegKey HKLM "Software\TensorLay" "InstallDir"
RequestExecutionLevel admin
Unicode True
SetCompressor /SOLID lzma

; ─── Version ───
!define VERSION "0.9.23"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "TensorLay"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "TensorLay Setup"
VIAddVersionKey "LegalCopyright" "TensorLay"
VIAddVersionKey "FileVersion" "${VERSION}"

; ─── Variables ───
Var DesktopCheck
Var TaskbarCheck

; ─── Branded MUI2 ───
!define MUI_ICON "TensorLay\Resources\Icons\app.ico"
!define MUI_UNICON "TensorLay\Resources\Icons\app.ico"

; Sidebar bitmap for Welcome + Finish pages
!define MUI_WELCOMEFINISHPAGE_BITMAP "installer\sidebar.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "installer\sidebar.bmp"

; Header bitmap for Directory + Install pages
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_BITMAP "installer\header.bmp"
!define MUI_HEADERIMAGE_RIGHT

; Colors
!define MUI_BGCOLOR "FAFAF7"
!define MUI_TEXTCOLOR "1A1A1A"

!define MUI_ABORTWARNING

; ─── Pages ───
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY

; Custom options page
Page custom OptionsPage OptionsPageLeave

!insertmacro MUI_PAGE_INSTFILES

; Don't pass MUI_FINISHPAGE_RUN as a path — NSIS would Exec it directly,
; inheriting the installer's elevated token, and TensorLay would run as
; admin. Any service install (git clone, pip) it then performs would
; create files owned by BUILTIN/Administrators, which later breaks Forge's
; own git operations under the user account ("dubious ownership").
; Trampoline through explorer.exe to drop elevation: Explorer always runs
; as the interactive user (medium IL), so the app inherits the user's
; token, not ours.
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION "RunAppNotElevated"
!define MUI_FINISHPAGE_RUN_TEXT "Launch TensorLay"
!insertmacro MUI_PAGE_FINISH

Function RunAppNotElevated
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\TensorLay.exe"'
FunctionEnd

; Uninstaller
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ─── Custom Options Page ───
Function OptionsPage
    nsDialogs::Create 1018
    Pop $0

    ; Title
    ${NSD_CreateLabel} 0 0 100% 18u "Additional Options"
    Pop $0
    CreateFont $1 "Segoe UI" 12 700
    SendMessage $0 ${WM_SETFONT} $1 0

    ; Subtitle
    ${NSD_CreateLabel} 0 22u 100% 14u "Choose shortcuts to create:"
    Pop $0

    ; Desktop shortcut checkbox
    ${NSD_CreateCheckbox} 0 48u 100% 16u "Create desktop shortcut"
    Pop $DesktopCheck
    ${NSD_Check} $DesktopCheck
    CreateFont $2 "Segoe UI" 9 400
    SendMessage $DesktopCheck ${WM_SETFONT} $2 0

    ; Taskbar pin checkbox
    ${NSD_CreateCheckbox} 0 68u 100% 16u "Pin to taskbar"
    Pop $TaskbarCheck
    ${NSD_Check} $TaskbarCheck
    SendMessage $TaskbarCheck ${WM_SETFONT} $2 0

    nsDialogs::Show
FunctionEnd

Function OptionsPageLeave
    ${NSD_GetState} $DesktopCheck $0
    ${NSD_GetState} $TaskbarCheck $1
    StrCpy $DesktopCheck $0
    StrCpy $TaskbarCheck $1
FunctionEnd

; ─── Init ───
Function .onInit
    ; Kill running instance silently
    nsExec::ExecToLog 'taskkill /f /im TensorLay.exe' /TIMEOUT=3000
    nsExec::ExecToLog 'taskkill /f /im GpuHub.exe' /TIMEOUT=3000
FunctionEnd

; ─── Install ───
Section "TensorLay" SecMain
    SectionIn RO
    SetOutPath "$INSTDIR"

    ; Remove old files if upgrading
    Delete "$INSTDIR\TensorLay.exe"
    Delete "$INSTDIR\GpuHub.exe"

    ; Install main exe
    File "/oname=TensorLay.exe" "publish\GpuHub.exe"

    ; Data directories
    CreateDirectory "$LOCALAPPDATA\TensorLay"
    CreateDirectory "$LOCALAPPDATA\TensorLay\models"

    ; Start Menu (always)
    CreateDirectory "$SMPROGRAMS\TensorLay"
    CreateShortCut "$SMPROGRAMS\TensorLay\TensorLay.lnk" "$INSTDIR\TensorLay.exe" "" "$INSTDIR\TensorLay.exe" 0
    CreateShortCut "$SMPROGRAMS\TensorLay\Uninstall.lnk" "$INSTDIR\uninstall.exe" "" "$INSTDIR\uninstall.exe" 0

    ; Desktop shortcut
    StrCmp $DesktopCheck ${BST_CHECKED} 0 +2
        CreateShortCut "$DESKTOP\TensorLay.lnk" "$INSTDIR\TensorLay.exe" "" "$INSTDIR\TensorLay.exe" 0

    ; Taskbar pin via PowerShell
    StrCmp $TaskbarCheck ${BST_CHECKED} 0 +3
        nsExec::ExecToLog 'powershell -ExecutionPolicy Bypass -Command "& { $$s = New-Object -ComObject Shell.Application; $$f = $$s.Namespace(''$INSTDIR''); $$i = $$f.ParseName(''TensorLay.exe''); $$i.InvokeVerb(''taskbarpin'') }"'
        Pop $0

    ; Registry
    WriteRegStr HKLM "Software\TensorLay" "InstallDir" "$INSTDIR"
    WriteRegStr HKLM "Software\TensorLay" "Version" "${VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "DisplayName" "TensorLay ${VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "DisplayIcon" "$INSTDIR\TensorLay.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "UninstallString" '"$INSTDIR\uninstall.exe"'
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "Publisher" "TensorLay"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "DisplayVersion" "${VERSION}"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "NoRepair" 1

    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
    IntFmt $0 "0x%08X" $0
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay" \
        "EstimatedSize" "$0"

    ; ─── Register tensorlay:// URL scheme (for OAuth callback from website) ───
    WriteRegStr HKCR "tensorlay" "" "URL:TensorLay Protocol"
    WriteRegStr HKCR "tensorlay" "URL Protocol" ""
    WriteRegStr HKCR "tensorlay\DefaultIcon" "" '"$INSTDIR\TensorLay.exe",1'
    WriteRegStr HKCR "tensorlay\shell" "" "open"
    WriteRegStr HKCR "tensorlay\shell\open\command" "" '"$INSTDIR\TensorLay.exe" "%1"'

    WriteUninstaller "$INSTDIR\uninstall.exe"
SectionEnd

; ─── Uninstall ───
Section "Uninstall"
    nsExec::ExecToLog 'taskkill /f /im TensorLay.exe'

    Delete "$INSTDIR\TensorLay.exe"
    Delete "$INSTDIR\TensorLay.exe.backup"
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"

    Delete "$SMPROGRAMS\TensorLay\TensorLay.lnk"
    Delete "$SMPROGRAMS\TensorLay\Uninstall.lnk"
    RMDir "$SMPROGRAMS\TensorLay"
    Delete "$DESKTOP\TensorLay.lnk"

    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\TensorLay"
    DeleteRegKey HKLM "Software\TensorLay"
    DeleteRegKey HKCR "tensorlay"
SectionEnd
