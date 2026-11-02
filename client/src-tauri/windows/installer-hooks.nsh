; Hooks into Tauri's NSIS installer (bundle.windows.nsis.installerHooks in tauri.conf.json).
;
; winget, and anything else that runs QuietUninstallString, waits for the process it starts. Run plainly, an NSIS
; uninstaller copies itself to %TEMP%, starts the copy, and exits at once, so winget reported success while the
; uninstall was still going on. With _?=<folder> NSIS runs the uninstaller in place, so the process winget waits for is
; the one doing the work. A running uninstaller cannot delete itself, so /CLEANUP asks it to remove uninstall.exe and
; the folder once it has exited. Tauri's own reinstall also runs the old uninstaller with _?=, then writes the new
; version into the same folder, so the cleanup runs only for /CLEANUP. packaging/winget/README.md has the details.
; Tauri's template includes FileFunc.nsh (GetOptions) and LogicLib.nsh before this file.

!macro NSIS_HOOK_POSTINSTALL
  WriteRegStr SHCTX "${UNINSTKEY}" "QuietUninstallString" "$\"$INSTDIR\uninstall.exe$\" /S /CLEANUP _?=$INSTDIR"
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ClearErrors
  ${GetOptions} $CMDLINE "/CLEANUP" $R0
  ${IfNot} ${Errors}
    ; Tries every second for 30 seconds, until this uninstaller has exited and released its file. RMDir is not
    ; recursive: the folder stays if anything else is in it (the HyperHarbor host's tray keeps TrayWebView there).
    ; ExecShell starts cmd in the output folder, which is the install folder here, and a process's working folder
    ; cannot be removed; so it starts in %TEMP%.
    SetOutPath "$TEMP"
    ExecShell "" "$SYSDIR\cmd.exe" `/c cd /d "$TEMP" & for /l %i in (1,1,30) do @(ping -n 2 127.0.0.1 >nul & del /f /q "$INSTDIR\uninstall.exe" 2>nul & if not exist "$INSTDIR\uninstall.exe" (rmdir "$INSTDIR" 2>nul & exit))` SW_HIDE
  ${EndIf}
!macroend
