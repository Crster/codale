param([ValidateSet('x64','ARM64')][string]$Platform = 'x64')
$ErrorActionPreference = 'Stop'
$revision = '8642541b255bfb5dc39cd351a178be09013a55d6'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$cache = "$root/.cache/smart"
$output = "$PSScriptRoot/bin/win-$($Platform.ToLowerInvariant())"
New-Item -ItemType Directory -Force $cache,$output | Out-Null
$arch = $Platform.ToLowerInvariant()
$archive = "$cache/vulkan-$arch.zip"
$digest = if ($arch -eq 'x64') { 'bac6914b5e83523eb008c6789eaa12669f93fbc196c3b9e8237b6672323b33e9' } else { 'cd375e330452496aadf8a872cde6d48c92e51d598088e5575a32a0d43bc0c9f0' }
if (!(Test-Path $archive)) { Invoke-WebRequest "https://github.com/ggml-org/llama.cpp/releases/download/b11960/llama-b11960-bin-win-vulkan-$arch.zip" -OutFile $archive }
if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $digest) { throw 'Native archive checksum mismatch.' }
Expand-Archive -Force $archive "$cache/vulkan-$arch"
Get-ChildItem "$cache/vulkan-$arch" -Filter *.dll -Recurse | Copy-Item -Destination $output -Force
foreach ($header in 'llama.h','ggml.h','ggml-cpu.h','ggml-backend.h','ggml-opt.h','gguf.h','ggml-alloc.h') {
    $path = if ($header -eq 'llama.h') { 'include' } else { 'ggml/include' }
    Invoke-WebRequest "https://raw.githubusercontent.com/ggml-org/llama.cpp/$revision/$path/$header" -OutFile "$cache/$header"
}
$vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -property installationPath
if (!$vs) { throw 'Visual Studio C++ build tools are required.' }
$toolVersion = (Get-Content "$vs/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt").Trim()
if (!(Test-Path "$vs/VC/Tools/MSVC/$toolVersion/bin/Hostx64/$arch/cl.exe")) {
    throw "Install the Visual Studio C++ $Platform build tools before building the Smart native payload."
}
$devcmd = "$vs/Common7/Tools/VsDevCmd.bat"
$command = "`"$devcmd`" -arch=$arch -host_arch=x64 && cl /nologo /std:c++17 /LD /O2 /GS- /Zl /I`"$cache`" `"$PSScriptRoot/smart.cpp`" /Fo`"$output/smart.obj`" /link /NODEFAULTLIB /ENTRY:DllMain kernel32.lib /OUT:`"$output/codale-smart.dll`" /IMPLIB:`"$output/smart.lib`""
$batch = "$cache/build-$arch.cmd"
Set-Content -LiteralPath $batch -Value "@echo off`r`ncall $command" -Encoding Ascii
& cmd.exe /d /c $batch
if ($LASTEXITCODE -ne 0) { throw 'Smart native shim compilation failed.' }
