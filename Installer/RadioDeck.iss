; Inno Setup script (free) for RadioDeck
; Build: open this file in Inno Setup and click Compile
; Output: artifacts\installer

#define AppName "RadioDeck"
#define AppExeName "RadioDeck.exe"

; Update this if you publish/obfuscate to a different runtime folder.
#define SourceDir "..\\artifacts\\obf\\win-x64"

[Setup]
AppId={{CBDB850C-C1E9-412A-B2BB-1D31851EB450}
AppName={#AppName}
AppVersion=2.0.0
AppPublisher={#AppName}
DefaultDirName={autopf}\\{#AppName}
DefaultGroupName={#AppName}
OutputDir=..\\artifacts\\installer
OutputBaseFilename=RadioDeckSetup
SetupIconFile=..\\radio.ico
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\\radio.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\\{#AppName}"; Filename: "{app}\\{#AppExeName}"; IconFilename: "{app}\\radio.ico"
Name: "{commondesktop}\\{#AppName}"; Filename: "{app}\\{#AppExeName}"; IconFilename: "{app}\\radio.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
