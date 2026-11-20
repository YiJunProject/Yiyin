param([Parameter(Mandatory)][string]$PublishRoot,[string]$Compiler='ISCC.exe')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$payload=(Resolve-Path -LiteralPath $PublishRoot).Path
foreach($name in @('Yiyin.exe','Yiyin.dll','Yiyin.Core.dll','PresentationFramework.dll','audio-native\Yideck.Audio.Engine.exe','audio-native\Yideck.Audio.Scan.exe','Web\index.html','使用说明.txt','LICENSE','THIRD-PARTY-NOTICES.md')){
    if(!(Test-Path -LiteralPath (Join-Path $payload $name))){throw "Missing payload: $name"}
}
$out=Join-Path $root 'releases\v1.2.0'
if(Test-Path -LiteralPath (Join-Path $out 'Yiyin-v1.2.0-win-x64-Setup.exe')){throw 'Completed installer exists; preserve it and select a new version.'}
New-Item -ItemType Directory -Force -Path $out | Out-Null
& $Compiler "/DPublishDir=$payload" "/DOutputDir=$out" (Join-Path $root 'installer\Yiyin.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compile failed'}
