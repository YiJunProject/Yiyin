#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
[Setup]
AppId={{F80EB547-59E8-40B2-BA81-EE1676EB12D1}
AppName=易音
AppVersion=1.2.0
AppPublisher=噫菌
AppPublisherURL=https://github.com/YiJunProject/Yiyin
AppSupportURL=https://github.com/YiJunProject/Yiyin/issues
LicenseFile=..\LICENSE
VersionInfoDescription=易音音频机架安装程序
VersionInfoProductName=易音
DefaultDirName={localappdata}\Programs\Yiyin
DefaultGroupName=易音
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=Yiyin-v1.2.0-win-x64-Setup
SetupIconFile=..\assets\yiyin.ico
UninstallDisplayIcon={app}\Yiyin.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
ChangesAssociations=yes
UsePreviousAppDir=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\易音"; Filename: "{app}\Yiyin.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\易音"; Filename: "{app}\Yiyin.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\.yiyin"; ValueType: string; ValueData: "Yiyin.Project"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Yiyin.Project"; ValueType: string; ValueData: "易音工程"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Yiyin.Project\DefaultIcon"; ValueType: string; ValueData: """{app}\Yiyin.exe"",0"
Root: HKCU; Subkey: "Software\Classes\Yiyin.Project\shell\open\command"; ValueType: string; ValueData: """{app}\Yiyin.exe"" ""%1"""

[Run]
Filename: "{app}\Yiyin.exe"; Description: "启动易音"; Flags: nowait postinstall skipifsilent
