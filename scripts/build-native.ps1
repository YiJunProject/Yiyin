param([string]$Output='native-audio/bin-v1.2.0',[string]$Dependencies,[string]$BuildDirectory='native-audio/build-v120',[switch]$EnableAsio,[string]$AsioInclude)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Dependencies){$Dependencies=Join-Path $root 'third_party'}
$outputPath=if([IO.Path]::IsPathRooted($Output)){$Output}else{Join-Path $root $Output}
$buildPath=if([IO.Path]::IsPathRooted($BuildDirectory)){$BuildDirectory}else{Join-Path $root $BuildDirectory}
if($EnableAsio -and !(Test-Path -LiteralPath (Join-Path $AsioInclude 'asio.h'))){throw 'Provide separately licensed ASIO headers with -AsioInclude.'}
$asio=if($EnableAsio){'ON'}else{'OFF'}
cmake -S "$root/native-audio" -B $buildPath -G 'Visual Studio 17 2022' -A x64 "-DDEPS=$Dependencies" "-DYIDECK_ENABLE_ASIO=$asio" "-DASIO_INCLUDE=$AsioInclude" "-DYIDECK_RUNTIME_DIR=$outputPath"
if($LASTEXITCODE){throw 'Native configure failed'}
cmake --build $buildPath --config Release --parallel 6 --target Yideck.Audio.Engine Yideck.Audio.Scan Yideck.Test.GraphEdits Yideck.Test.VST2.Live Yideck.Test.VST3.Editor Yiyin.Test.VST3.Once
if($LASTEXITCODE){throw 'Native build failed'}
$base=Get-Content -LiteralPath "$root/native-audio/dependencies.v1.0.1.lock.json" -Raw | ConvertFrom-Json
$backends=@('WASAPI');if($EnableAsio){$backends+= 'ASIO'}
$dependenciesList=@($base.dependencies | Where-Object {$_.name -ne 'Steinberg ASIO SDK interface'})
if($EnableAsio){
    $asioMetadata=$base.dependencies | Where-Object name -eq 'Steinberg ASIO SDK interface'
    $asioMetadata.authorization=[ordered]@{basis='Builder is responsible for its applicable ASIO agreement. Official Yiyin release authorization confirmed by product owner on 2026-11-16.';privateAgreementIncluded=$false}
    foreach($name in @('asio.h','asiosys.h','iasiodrv.h')){$asioMetadata.interfaceSha256.$name=(Get-FileHash -LiteralPath (Join-Path $AsioInclude $name)).Hash.ToLowerInvariant()}
    $dependenciesList+=$asioMetadata
}
$binaries=[ordered]@{};foreach($name in @('Yideck.Audio.Engine.exe','Yideck.Audio.Scan.exe')){$binaries[$name]=(Get-FileHash -LiteralPath (Join-Path $outputPath $name)).Hash.ToLowerInvariant()}
$record=[ordered]@{schema=1;product='Yiyin.Audio';version='1.2.0';asBuiltAsio=[bool]$EnableAsio;dependencies=$dependenciesList;build=[ordered]@{architecture='Windows x64';configuration='Release';msvcRuntime='static /MT';vst3StaticCrt=$true;enabledBackends=$backends;productVersion='1.2.0.0';runtimeDirectory=$Output};patches=$base.patches;binaries=$binaries;standalonePatch=$base.standalonePatch}
$record | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath "$root/native-audio/dependencies.v1.2.0.lock.json" -Encoding utf8