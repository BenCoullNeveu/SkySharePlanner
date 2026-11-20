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
; NINA deploys this folder into Plugins\3.0.0 when it starts (NINA.Plugin PluginLoader.DeployFromStaging)
#define StagingDir "{localappdata}\NINA\PluginStaging\3.0.0\SkyShare Planner"

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
; Logo: installer/SkyShareLogo.png; .ico and .bmp are generated from it
SetupIconFile=SkyShareLogo.ico
UninstallDisplayIcon={uninstallexe}
WizardSmallImageFile=WizardSmall.bmp
ArchitecturesAllowed=x64compatible
; NINA can stay open. While it runs, the files go to NINA's own plugin staging
; folder (the one its plugin manager uses for updates), and NINA moves them into
; place on its next start, before it loads any plugin. So never ask to close NINA.
CloseApplications=no
RestartApplications=no

[Files]
; NINA closed: straight into the plugin folder
Source: "{#SourceDir}\SkyShare.Planner.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: not NinaRunning
Source: "{#SourceDir}\SkyShare.Planner.pdb"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist; Check: not NinaRunning
; NINA running: staged; NINA deploys it at its next start
Source: "{#SourceDir}\SkyShare.Planner.dll"; DestDir: "{#StagingDir}"; Flags: ignoreversion; Check: NinaRunning
Source: "{#SourceDir}\SkyShare.Planner.pdb"; DestDir: "{#StagingDir}"; Flags: ignoreversion skipifsourcedoesntexist; Check: NinaRunning

[UninstallDelete]
; Staged files reach {app} through NINA, so the uninstall log doesn't list them there
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{#StagingDir}"

[Messages]
FinishedLabel=SkyShare Planner has been installed.%n%nRestart N.I.N.A. - a "SkyShare" category will appear in the sequencer's instruction list.

[Code]
var
  NinaChecked, NinaWasRunning: Boolean;

{ Decided once, so every file goes to the same place even if NINA starts or stops mid-install. }
function NinaRunning(): Boolean;
var
  Locator, Service, Procs: Variant;
begin
  if not NinaChecked then begin
    NinaChecked := True;
    try
      Locator := CreateOleObject('WbemScripting.SWbemLocator');
      Service := Locator.ConnectServer('.', 'root\CIMV2');
      Procs := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''NINA.exe''');
      NinaWasRunning := Procs.Count > 0;
    except
      { Can't tell: staging is always safe, NINA deploys it on its next start either way }
      NinaWasRunning := True;
    end;
  end;
  Result := NinaWasRunning;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { A direct install clears an older staged copy, or NINA would put it back over this one on start }
  if (CurStep = ssInstall) and not NinaRunning() then
    DelTree(ExpandConstant('{#StagingDir}'), True, True, True);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and NinaRunning() then
    WizardForm.FinishedLabel.Caption :=
      'SkyShare Planner {#AppVersion} is ready.' + #13#10#13#10 +
      'N.I.N.A. is running, so the update was put in N.I.N.A.''s plugin staging folder. ' +
      'It takes effect the next time N.I.N.A. starts; the session that is open now keeps the current version.';
end;
