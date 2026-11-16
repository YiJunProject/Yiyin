param([string]$Destination)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Destination){$Destination=Join-Path $root 'third_party'}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$dependencies=@(
    @{Name='rtaudio-06f97b633c06ea747d6478f0e87b6e1178104511';Url='https://github.com/thestk/rtaudio.git';Commit='06f97b633c06ea747d6478f0e87b6e1178104511';Patch='rtaudio-wasapi.patch'},
    @{Name='vst2sdk-339d4f31590bf77c0d0d248e09a380ac6285e069';Url='https://github.com/Xaymar/vst2sdk.git';Commit='339d4f31590bf77c0d0d248e09a380ac6285e069'},
    @{Name='vst3sdk';Url='https://github.com/steinbergmedia/vst3sdk.git';Commit='3cdf9ca5d1f5b1b21e0a86832aa4abe55607bd96';Recursive=$true;Patch='vst3-utf8-path.patch'}
)
foreach($dependency in $dependencies){
    $target=Join-Path $Destination $dependency.Name
    if(Test-Path -LiteralPath $target){throw "Dependency path already exists: $target. Preserve it; choose an empty destination."}
    git init $target
    if($LASTEXITCODE){throw 'Git init failed'}
    git -C $target remote add origin $dependency.Url
    git -C $target fetch --depth 1 origin $dependency.Commit
    if($LASTEXITCODE){throw 'Dependency fetch failed'}
    git -C $target checkout --detach FETCH_HEAD
    if($LASTEXITCODE){throw 'Dependency checkout failed'}
    if($dependency.Recursive){
        git -C $target submodule update --init --recursive --depth 1
        if($LASTEXITCODE){throw 'VST3 submodule fetch failed'}
    }
    if($dependency.Patch){
        $patchTarget=if($dependency.Recursive){Join-Path $target 'public.sdk'}else{$target}
        git -C $patchTarget apply --check (Join-Path $root ('native-audio/patches/'+$dependency.Patch))
        if($LASTEXITCODE){throw 'Patch verification failed'}
        git -C $patchTarget apply (Join-Path $root ('native-audio/patches/'+$dependency.Patch))
        if($LASTEXITCODE){throw 'Patch failed'}
    }
}
Write-Output "Pinned dependencies prepared at $Destination. ASIO is disabled by default and requires separately licensed headers."
