param([string]$Output,[string]$DotnetExe='dotnet',[string]$NugetPackages)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Output){$Output='artifacts/publish/v1.1.0-'+(Get-Date -Format 'yyyyMMdd-HHmmss')}
$target=if([IO.Path]::IsPathRooted($Output)){$Output}else{Join-Path $root $Output}
if(Test-Path -LiteralPath $target){throw 'Publish directory exists. Use a fresh path to preserve frozen artifacts.'}
if([IO.Path]::IsPathRooted($DotnetExe)){$env:DOTNET_ROOT=Split-Path -Parent $DotnetExe}
$env:DOTNET_CLI_HOME=Join-Path $root 'artifacts/dotnet-home'
if($NugetPackages){$env:NUGET_PACKAGES=$NugetPackages}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
Push-Location -LiteralPath $root
try {
    & $DotnetExe publish 'src/Yiyin.App/Yiyin.App.csproj' -c Release -r win-x64 --self-contained true -o $target --nologo
    if($LASTEXITCODE -ne 0){throw 'Publish failed'}
} finally {Pop-Location}
