# Run app - build a signed MSIX, install it, and launch Codale
#
# Codale has to run *with* package identity: the Explorer right-click handler is an
# in-proc COM server declared in the appxmanifest, the codale: protocol activation is a
# package extension, and per-project state lives in the package's local folder. A loose
# unpackaged build has none of that, so this script always installs the packaged MSIX.
# The package is updated in place (the way Visual Studio deploys on F5), so settings and
# session state survive every run. When nothing has changed since the last deploy, the
# build and install are skipped entirely and the installed app is just relaunched.
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [string]$Configuration = "Debug",

    # Open a specific project, the way the Explorer context menu will. Needs the
    # codale: protocol extension (M2); without it the app opens on its launcher.
    [string]$ProjectPath,

    # Rebuild and reinstall even when the sources are unchanged.
    [switch]$Force,

    # Ship it: build an optimized Release package (ReadyToRun) and replace the installed
    # app with it. Implies -Configuration Release and -Force.
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

if ($Deploy) {
    $Configuration = "Release"
    $Force = $true
}

$projectDir = Join-Path $PSScriptRoot "src\Codale.App"
$csprojPath = Join-Path $projectDir "Codale.App.csproj"
$manifestPath = Join-Path $projectDir "Package.appxmanifest"
$rid = "win-$Platform"
$exeName = "Codale.App.exe"

# Identity Name from Package.appxmanifest. Get-AppxPackage matches on this, not DisplayName.
$packageId = "BB9ADD6B-2E64-46A7-ADF5-885B70F99D51"
$appId = "App"

# Must match <Identity Publisher> in the manifest or signing is rejected.
$publisher = "CN=Crster"
$certFriendlyName = "Codale Development"

# Codale is deliberately multi-instance (one window per project), so this stops every
# open window. They all lock the binaries the build is about to overwrite.
Get-Process -Name "Codale.App" -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($n in "Codale.Mcp.Browser", "Codale.Mcp.Computer", "Codale.Mcp.Tasks") { Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force }

# ---------------------------------------------------------------------------
# Signing certificate
# ---------------------------------------------------------------------------
# Signing needs the private key in the user store (no elevation), and installing the
# signed MSIX needs the public certificate trusted machine-wide (needs elevation once).
function Get-SigningCertificate {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($cert) {
        return $cert
    }

    Write-Host "No signing certificate for $publisher - creating a self-signed development certificate..."

    # CodeSigningCert + the MSIX EKU. Creating this in CurrentUser\My needs no elevation.
    return New-SelfSignedCertificate `
        -Type Custom `
        -Subject $publisher `
        -FriendlyName $certFriendlyName `
        -KeyUsage DigitalSignature `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)
}

function Confirm-CertificateTrusted {
    param([Parameter(Mandatory)] $Certificate)

    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $Certificate.Thumbprint

    if ($trusted) {
        return
    }

    # Windows will not install a signed MSIX whose signer it does not trust. Putting the
    # public certificate in LocalMachine\TrustedPeople is the one step that needs admin,
    # so do it in a single elevated call rather than making every later run elevated.
    $cerPath = Join-Path $env:TEMP "Codale_$($Certificate.Thumbprint).cer"
    Export-Certificate -Cert $Certificate -FilePath $cerPath -Force | Out-Null

    Write-Host "Certificate $($Certificate.Thumbprint) is not trusted on this machine."
    Write-Host "Requesting elevation to add it to Cert:\LocalMachine\TrustedPeople (one time)..."

    $command = "Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $elevated = Start-Process powershell.exe `
        -ArgumentList "-NoProfile", "-NonInteractive", "-Command", $command `
        -Verb RunAs -Wait -PassThru -ErrorAction SilentlyContinue

    Remove-Item $cerPath -ErrorAction SilentlyContinue

    $nowTrusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $Certificate.Thumbprint

    if (-not $nowTrusted) {
        Write-Host ""
        Write-Host "Could not trust the certificate (elevation declined or failed)."
        Write-Host "Run this once from an elevated PowerShell:"
        Write-Host ""
        Write-Host "  `$c = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq '$($Certificate.Thumbprint)'"
        Write-Host "  Export-Certificate -Cert `$c -FilePath `$env:TEMP\Codale.cer | Out-Null"
        Write-Host "  Import-Certificate -FilePath `$env:TEMP\Codale.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        Write-Host ""
        exit 1
    }

    Write-Host "Certificate trusted."
}

# ---------------------------------------------------------------------------
# ripgrep payload
# ---------------------------------------------------------------------------
# The search tab and the model's exploration loop run rg beside the app exe when it is
# there (RipgrepSearch.ResolveRipgrep), falling back to PATH. Shipping a copy makes
# search independent of what the user installed - and independent of winget's rg.exe,
# which is a 0-byte alias reparse point that some AV software refuses to spawn. The
# resolved real target is cached under tools\rg (not src - it is a payload, not source).
$rgTarget = Join-Path $PSScriptRoot "tools\rg\rg.exe"
if (-not (Test-Path $rgTarget)) {
    $rgOnPath = (Get-Command rg.exe -ErrorAction SilentlyContinue).Source
    if ($rgOnPath) {
        $rgResolved = (Get-Item $rgOnPath).Target
        if (-not $rgResolved) { $rgResolved = $rgOnPath }

        Write-Host "Caching ripgrep payload from $rgResolved ..."
        New-Item -ItemType Directory -Force -Path (Split-Path $rgTarget) | Out-Null
        Copy-Item $rgResolved $rgTarget
    } else {
        Write-Host "ripgrep not found on PATH; the app will rely on whatever the user has."
    }
}

# ---------------------------------------------------------------------------
# Change detection
# ---------------------------------------------------------------------------
# Hash the sources that actually go into the package (src\ plus the repo-level build
# files) so a later run can tell whether the installed package is already up to date.
# tests\ is excluded on purpose: editing a test should not force a redeploy.
function Get-SourceStamp {
    $roots = @((Join-Path $PSScriptRoot "src"))
    $files = Get-ChildItem -LiteralPath $roots -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|AppPackages|\.git|\.vs)(\\|$)' -and
            $_.Extension -notin ".log", ".md", ".user"
        }

    $files += Get-ChildItem -LiteralPath $PSScriptRoot -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in "Directory.Build.props", "global.json", "Codale.slnx" }

    $stampInput = ($files | Sort-Object FullName | ForEach-Object {
        "{0}={1}" -f $_.FullName.Substring($PSScriptRoot.Length + 1), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA1).Hash
    }) -join "`n"

    $stampStream = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($stampInput))
    return (Get-FileHash -InputStream $stampStream -Algorithm SHA1).Hash
}

function Start-Codale {
    param([Parameter(Mandatory)] $Package)

    if ($ProjectPath) {
        $full = [System.IO.Path]::GetFullPath($ProjectPath)
        $uri = "codale://open?path=$([uri]::EscapeDataString($full))"
        Write-Host "Launching $uri ..."
        Start-Process $uri
        return
    }

    # Activate through the package (AUMID) rather than running the exe from disk: that is
    # what gives the process package identity, which the shell extension, protocol
    # activation and ApplicationData all depend on.
    $aumid = "$($Package.PackageFamilyName)!$appId"
    Write-Host "Launching $aumid ..."
    Start-Process "explorer.exe" -ArgumentList "shell:AppsFolder\$aumid"
}

# ---------------------------------------------------------------------------
# Fast path: nothing changed, just relaunch
# ---------------------------------------------------------------------------
$stampFile = Join-Path $projectDir "obj\deploy-stamp.$Configuration-$Platform.txt"
$currentStamp = Get-SourceStamp
$package = Get-AppxPackage -Name $packageId

if (-not $Force -and $package -and (Test-Path $stampFile) -and (Get-Content $stampFile) -eq $currentStamp) {
    Write-Host "Sources unchanged since the last deploy - relaunching $($package.PackageFullName)."
    Start-Codale -Package $package
    exit 0
}

$certificate = Get-SigningCertificate
Confirm-CertificateTrusted -Certificate $certificate
Write-Host "Signing with $($certificate.Thumbprint) ($($certificate.Subject))."

# ---------------------------------------------------------------------------
# Version bump
# ---------------------------------------------------------------------------
# Windows refuses to update a package in place when the version is unchanged but the
# contents differ (0x80073CFB), and AppxAutoIncrementPackageRevision is a Visual Studio
# IDE feature - under a plain dotnet build the version stays pinned to the manifest.
# Bump the revision the way VS does on F5, so every run builds a higher version that
# replaces the installed package without touching its data.
$manifestText = Get-Content $manifestPath -Raw
$bumpedManifest = [regex]::Replace($manifestText, '(<Identity\b[^>]*\bVersion=")(\d+\.\d+\.\d+\.\d+)(")', {
    param($match)
    $version = [Version]$match.Groups[2].Value
    '{0}{1}.{2}.{3}.{4}{5}' -f $match.Groups[1].Value,
        $version.Major, $version.Minor, $version.Build, ($version.Revision + 1), $match.Groups[3].Value
})
if ($bumpedManifest -eq $manifestText) {
    Write-Host "Could not find an Identity Version to bump in Package.appxmanifest."
    exit 1
}
Set-Content -Path $manifestPath -Value $bumpedManifest -NoNewline -Encoding UTF8

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
# Visual Studio is needed twice below: to build the native shell extension, and so the
# packaging targets can find mspdbcmf.exe for the .appxsym (the bare dotnet CLI does not
# set VsInstallRoot for their lookup).
$vsRoot = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -property installationPath
}

# The Explorer context menu handler is a C++ vcxproj, and the .NET SDK's MSBuild cannot
# build those - it needs Visual Studio's MSBuild with the VC targets. Build it first so
# Codale.App can pick the DLL up as package content.
$shellExtProject = Join-Path $PSScriptRoot "src\Codale.ShellExt\Codale.ShellExt.vcxproj"
$vsMsBuild = $null
if ($vsRoot) {
    $vsMsBuild = Join-Path $vsRoot "MSBuild\Current\Bin\MSBuild.exe"
}

if ((Test-Path $shellExtProject) -and $vsMsBuild -and (Test-Path $vsMsBuild)) {
    Write-Host "Building shell extension ($Platform $Configuration)..."
    & $vsMsBuild $shellExtProject "-p:Configuration=$Configuration" "-p:Platform=$Platform" -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Shell extension build failed."
        exit 1
    }
} else {
    Write-Host "Skipping shell extension: Visual Studio MSBuild not found. The app will deploy without the Explorer context menu."
}

# The built-in MCP servers (browser, desktop control) ship the same way: built first,
# then packaged as folders beside the app exe.
foreach ($mcp in "Codale.Mcp.Browser", "Codale.Mcp.Computer", "Codale.Mcp.Tasks") {
    Write-Host "Building $mcp..."
    dotnet build (Join-Path $PSScriptRoot "src\$mcp\$mcp.csproj") -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "$mcp build failed."
        exit 1
    }
}

# Build the way Visual Studio deploys on F5: a plain incremental build that packs and
# signs the MSIX straight from the build layout. dotnet publish adds nothing for local
# debug deploys - it recopies the entire output into a publish\ folder and runs
# ReadyToRun crossgen on every assembly - so it is only used for Release, where those
# publishing features matter.
Write-Host "Building signed package for $Platform ($Configuration)..."
$buildArgs = @(
    $csprojPath,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$rid",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxBundle=Never",
    "-p:AppxPackageSigningEnabled=true",
    "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)"
)

if ($vsRoot) {
    $buildArgs += "-p:VsInstallRoot=$vsRoot"
}

if ($Configuration -eq "Debug") {
    # Debug layouts land in obj\appx instead of AppPackages so the release folder stays
    # clean; the package search below already scans obj.
    $buildArgs += "-p:AppxPackageDir=$(Join-Path $projectDir "obj\appx")\"
    dotnet build @buildArgs
} else {
    dotnet publish @buildArgs
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed."
    exit 1
}

# ---------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------
# Packaging drops the main package and its scale resource packages into the build
# layout; the exact folder varies with how the build was invoked, and a previous run's
# artifacts may still be lying around, so take the newest main package.
$searchRoots = @(
    (Join-Path $projectDir "bin"),
    (Join-Path $projectDir "obj"),
    (Join-Path $projectDir "AppPackages")
) | Where-Object { Test-Path $_ }

$candidates = Get-ChildItem -Path $searchRoots -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue
$mainPackage = $candidates |
    Where-Object { $_.Name -notmatch '_scale-\d+' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $mainPackage) {
    Write-Host "No .msix found under bin\, obj\ or AppPackages\."
    exit 1
}
$resources = Get-ChildItem -Path $mainPackage.DirectoryName -Filter "*_scale-*.msix" -ErrorAction SilentlyContinue

Write-Host "Installing $($mainPackage.Name)..."

# Update the existing package in place instead of removing it first - Remove-AppxPackage
# deletes the package's application data (LocalState plus the virtualized %APPDATA% copy
# inside the MSIX container), which would reset settings and the session index on every
# deploy. -ForceUpdateFromAnyVersion also lets this deploy replace a newer installed
# package (for example one deployed from Visual Studio) without a remove.
$install = @{ Path = $mainPackage.FullName; ForceUpdateFromAnyVersion = $true }
if ($resources) {
    $install["DependencyPath"] = $resources.FullName
}

try {
    Add-AppxPackage @install
} catch {
    # A development-mode (loose) registration or a staged package cannot be updated by a
    # signed MSIX - that is the one case where the old install has to be removed first,
    # losing package data. Everything else re-registers in place above.
    $existing = Get-AppxPackage -Name $packageId
    if (-not $existing) {
        throw
    }
    Write-Host "In-place update failed: $($_.Exception.Message)"
    Write-Host "Removing existing package $($existing.PackageFullName) and reinstalling..."
    Remove-AppxPackage -Package $existing.PackageFullName
    Add-AppxPackage @install
}

$package = Get-AppxPackage -Name $packageId
if (-not $package) {
    Write-Host "Package installation failed."
    exit 1
}
Write-Host "Installed: $($package.PackageFullName) (SignatureKind: $($package.SignatureKind))"

# Record the state that produced this deploy (recomputed after the version bump, so the
# stored hash matches the manifest as built) - the next unchanged run can then skip
# straight to relaunching. Also keep only the current debug layout: every versioned
# layout is a couple hundred megabytes of churn.
Set-Content -Path $stampFile -Value (Get-SourceStamp)
if ($Configuration -eq "Debug") {
    Get-ChildItem -Path (Join-Path $projectDir "obj\appx") -Directory -Filter "Codale.App_*" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $mainPackage.DirectoryName } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

Start-Codale -Package $package
Write-Host "Done."
