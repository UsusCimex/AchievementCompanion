; Per-user setup without admin rights, into %LocalAppData%\Programs; the version comes from ISCC /DAppVersion=X.Y.Z.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppExe "AchievementCompanion.exe"
#define SideImages "side-202.png,side-404.png"
#define SmallImages "small-58.png,small-77.png,small-97.png,small-124.png"

[Setup]
; The same AppId on every release makes a new setup update the installed copy.
AppId={{39FB1325-FFAF-4F0B-B07A-70CD681B2423}
AppName=Achievement Companion
AppVersion={#AppVersion}
AppPublisher=UsusCimex
AppPublisherURL=https://github.com/UsusCimex/AchievementCompanion
AppSupportURL=https://github.com/UsusCimex/AchievementCompanion/issues
AppUpdatesURL=https://github.com/UsusCimex/AchievementCompanion/releases
DefaultDirName={autopf}\Achievement Companion
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
DisableReadyPage=yes
ShowLanguageDialog=auto
WizardStyle=modern dynamic
WizardImageFile={#SideImages}
WizardImageFileDynamicDark={#SideImages}
WizardSmallImageFile={#SmallImages}
WizardSmallImageFileDynamicDark={#SmallImages}
SetupIconFile=..\src\AC.App\Assets\app.ico
UninstallDisplayName=Achievement Companion
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\out
OutputBaseFilename=AchievementCompanion-v{#AppVersion}-win-x64-setup
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Achievement Companion"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Achievement Companion"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Achievement Companion}"; Flags: nowait postinstall skipifsilent
