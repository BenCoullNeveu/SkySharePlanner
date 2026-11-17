; Inno Setup script for the SkyShare Planner NINA plugin.
; Built by .github/workflows/release.yml; to build locally (after a Release build):
;   iscc /DAppVersion=0.1.0 installer\SkySharePlanner.iss
; Output: dist\SkySharePlanner-Setup-<version>.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\nina-plugin\SkyShare.Planner\bin\Release\net8.0-windows"
#endif

[Setup]
AppId={{6F3B2C1A-8E4D-4B7F-9A2E-5C1D7B3E9F40}
AppName=SkyShare Planner for N.I.N.A.
AppVersion={#AppVersion}
AppPublisher=SkyShare
AppPublisherURL=https://github.com/BenCoullNeveu/SkySharePlanner
; NINA 3.x loads plugins from this per-user folder, so no admin rights are needed
PrivilegesRequired=lowest
DefaultDirName={localappdata}\NINA\Plugins\3.0.0\SkyShare Planner
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
; Keep the uninstaller out of the plugin folder NINA scans
UninstallFilesDir={localappdata}\SkyShare Planner\Uninstall
UninstallDisplayName=SkyShare Planner (NINA plugin)
OutputDir=..\dist
OutputBaseFilename=SkySharePlanner-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
; NINA locks the DLL while running: offer to close it on upgrade
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\SkyShare.Planner.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SkyShare.Planner.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Messages]
FinishedLabel=SkyShare Planner has been installed.%n%nRestart N.I.N.A. - a "SkyShare" category will appear in the sequencer's instruction list.
