; 声桥 SoundBridge 安装程序（NSIS 3.x，无需管理员权限）
;
; 使用 tools\build-installer.ps1 生成安装包和卸载文件列表。

Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!define APP_NAME    "声桥 SoundBridge"
!define APP_SHORT   "声桥"
!define APP_EXE     "SoundBridge.exe"
!define APP_VERSION "1.0.0"
!define APP_PUBLISHER "SoundBridge"
!define UNINST_KEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundBridge"

!ifndef OUTNAME
  !define OUTNAME "SoundBridge-Setup-${APP_VERSION}.exe"
!endif

!ifndef PAYLOAD
  !define PAYLOAD "..\dist\SoundBridge"
!endif
!ifndef UNINSTALL_FILES
  !error "Run tools/build-installer.ps1 to generate the uninstall file list."
!endif

Name "${APP_NAME}"
OutFile "..\dist\${OUTNAME}"
InstallDir "$LOCALAPPDATA\Programs\SoundBridge"
InstallDirRegKey HKCU "Software\SoundBridge" "InstallDir"
RequestExecutionLevel user
; 自包含单文件载荷接近 200 MB，LZMA 需要把整块输入 mmap 进内存
; （makensis 为 32 位，实测报 "Internal compiler error #12345: error creating mmap"），
; 因此便携版改用流式 zlib。
!ifdef PORTABLE
  SetCompressor zlib
!else
  SetCompressor /SOLID lzma
!endif

VIProductVersion "1.0.0.0"
VIAddVersionKey /LANG=2052 "ProductName"     "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"     "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"  "${APP_VERSION}"

!define MUI_ABORTWARNING
!define MUI_ICON   "..\src\SoundBridge.App\Assets\App.ico"
!define MUI_UNICON "..\src\SoundBridge.App\Assets\App.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即启动${APP_SHORT}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

!macro CheckExecutableAvailable
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    ; Ask for write access without truncating or otherwise modifying the file.
    System::Call 'kernel32::CreateFileW(w "$INSTDIR\${APP_EXE}", i 0x40000000, i 7, p 0, i 3, i 0x80, p 0) p .r0'
    ${If} $0 == -1
      MessageBox MB_ICONEXCLAMATION|MB_OK "无法替换声桥。请先从托盘退出程序并确认安装目录可写，然后重试。"
      Abort
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
  ${EndIf}
!macroend

Section "主程序（必需）" SecMain
  SectionIn RO

  !insertmacro CheckExecutableAvailable

  !ifndef PORTABLE
  ; 依赖运行时版：确认 .NET Desktop Runtime 11 (x64) 已安装（查 sharedfx 注册表值）
  SetRegView 64
  StrCpy $R8 0
  StrCpy $R9 0
  runtime_loop:
    EnumRegValue $R7 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $R8
    StrCmp $R7 "" runtime_done
    StrCpy $R6 $R7 3
    StrCmp $R6 "11." 0 runtime_next
      StrCpy $R9 1
      Goto runtime_done
    runtime_next:
    IntOp $R8 $R8 + 1
    Goto runtime_loop
  runtime_done:
  StrCmp $R9 1 runtime_ok
    MessageBox MB_ICONSTOP|MB_YESNO "未检测到 .NET Desktop Runtime 11（x64）。$\r$\n$\r$\n是否打开微软下载页面？" IDNO runtime_abort
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/11.0"
  runtime_abort:
    Abort
  runtime_ok:
  SetRegView 32
  !endif

  SetOutPath "$INSTDIR"
  File /r /x *.pdb /x log.txt /x SoundBridge.log /x startup-crash.txt "${PAYLOAD}\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS"
  CreateShortCut "$SMPROGRAMS\${APP_SHORT}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0

  WriteRegStr HKCU "Software\SoundBridge" "InstallDir" "$INSTDIR"

  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion"  "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"

  ; 回读校验：注册表被策略拦截时给出明确提示（否则"应用和功能"里看不到卸载入口）
  ReadRegStr $R5 HKCU "${UNINST_KEY}" "UninstallString"
  StrCmp $R5 "" 0 uninst_ok
    MessageBox MB_ICONEXCLAMATION|MB_OK "安装已完成，但写入卸载信息失败。$\r$\n程序位于 $INSTDIR，可手动删除该文件夹卸载。"
  uninst_ok:
SectionEnd

Section "桌面快捷方式" SecDesktop
  CreateShortCut "$DESKTOP\${APP_SHORT}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Section /o "开机自动启动" SecAutostart
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SoundBridge" '"$INSTDIR\${APP_EXE}"'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain}      "主程序文件与开始菜单快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop}   "在桌面创建快捷方式。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecAutostart} "登录 Windows 后自动在托盘启动${APP_SHORT}。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  !insertmacro CheckExecutableAvailable

  Delete "$DESKTOP\${APP_SHORT}.lnk"
  Delete "$SMPROGRAMS\${APP_SHORT}.lnk"

  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SoundBridge"
  ${If} $0 == '"$INSTDIR\${APP_EXE}"'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "SoundBridge"
  ${EndIf}
  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "Software\SoundBridge"

  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
