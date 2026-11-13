[CmdletBinding()]
param(
    [string]$Publisher = 'CN=SwSearch Local Package Publisher',
    [string]$Version = '1.3.16.0',
    [string]$ProductVersion = '1.3.16',
    [string]$PfxPath = '',
    [string]$PfxPasswordEnvironmentVariable = '',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\modern-context-menu')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'src\SwSearch.ModernContextMenu'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw 'MODERN_MENU_TOOLCHAIN_MISSING: vswhere.exe was not found.'
}
$installationPath = (& $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.Windows11SDK.26100 `
    -property installationPath | Select-Object -First 1)
if (-not $installationPath) {
    throw 'MODERN_MENU_TOOLCHAIN_MISSING: MSVC x64 and Windows 11 SDK 26100 are required.'
}
$vcvars = Join-Path $installationPath 'VC\Auxiliary\Build\vcvars64.bat'
$sdkBin = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'x64\MakeAppx.exe') } |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdkBin -or -not (Test-Path -LiteralPath $vcvars -PathType Leaf)) {
    throw 'MODERN_MENU_TOOLCHAIN_MISSING: vcvars64.bat/MakeAppx.exe/SignTool.exe were not found.'
}
$makeAppx = Join-Path $sdkBin.FullName 'x64\MakeAppx.exe'
$signTool = Join-Path $sdkBin.FullName 'x64\SignTool.exe'
foreach ($tool in @($makeAppx, $signTool)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "MODERN_MENU_TOOLCHAIN_MISSING: $tool" }
}

$parsedVersion = [Version]$Version
if ($parsedVersion.Major -ne 1 -or $parsedVersion.Minor -ne 3 -or $parsedVersion.Build -lt 0 -or $parsedVersion.Revision -lt 0) {
    throw "Modern context-menu version must be a four-part 1.3.x.x version: $Version"
}
$versionComma = "$($parsedVersion.Major),$($parsedVersion.Minor),$($parsedVersion.Build),$($parsedVersion.Revision)"
$privatePfx = $null
$password = $null
$certificate = $null
$publicThumbprint = $null

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $output | Out-Null
$work = Join-Path $output '.private-build'
$layout = Join-Path $work 'msix-layout'
New-Item -ItemType Directory -Path $layout -Force | Out-Null

try {
    if ([string]::IsNullOrWhiteSpace($PfxPath)) {
        $password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        $privatePfx = Join-Path $work 'signing-private-key.pfx'
        $rsa = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
                $Publisher,
                $rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
                    [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
            $eku = [Security.Cryptography.OidCollection]::new()
            [void]$eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add(
                [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $true))
            $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddYears(5))
            [IO.File]::WriteAllBytes(
                $privatePfx,
                $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $password))
        }
        finally { if ($rsa) { $rsa.Dispose() } }
    }
    else {
        $privatePfx = [IO.Path]::GetFullPath($PfxPath)
        if ($privatePfx.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'PFX/private keys must never be stored inside the repository.'
        }
        if (-not (Test-Path -LiteralPath $privatePfx -PathType Leaf)) { throw "Signing PFX not found: $privatePfx" }
        $password = [Environment]::GetEnvironmentVariable($PfxPasswordEnvironmentVariable)
        if ([string]::IsNullOrEmpty($password)) { throw "Signing password variable is empty: $PfxPasswordEnvironmentVariable" }
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($privatePfx, $password)
    }
    if ($certificate.Subject -cne $Publisher) {
        throw "Manifest Publisher must exactly equal certificate Subject. certificate='$($certificate.Subject)' manifest='$Publisher'"
    }

    $certificatePath = Join-Path $output 'SwSearch.ModernContextMenu.cer'
    [IO.File]::WriteAllBytes(
        $certificatePath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $publicThumbprint = $certificate.Thumbprint

    $resourceTemplate = [IO.File]::ReadAllText((Join-Path $source 'Version.rc.in'))
    $nativeTargets = @(
        [pscustomobject]@{ Name='SwSearch.ModernContextMenu.dll'; Source='ExplorerCommand.cpp'; Description='SwSearch Windows 11 Explorer command'; Type='VFT_DLL' },
        [pscustomobject]@{ Name='SwSearch.ModernContextMenuRegistration.exe'; Source='PackageRegistration.cpp'; Description='SwSearch modern context-menu registration helper'; Type='VFT_APP' }
    )
    foreach ($target in $nativeTargets) {
        $baseName = [IO.Path]::GetFileNameWithoutExtension($target.Name)
        $rcPath = Join-Path $work "$baseName.rc"
        $resPath = Join-Path $work "$baseName.res"
        $resource = $resourceTemplate.Replace('@@VERSION_COMMA@@', $versionComma).
            Replace('@@VERSION@@', $Version).
            Replace('@@PRODUCT_VERSION@@', $ProductVersion).
            Replace('@@ORIGINAL_FILENAME@@', $target.Name).
            Replace('@@DESCRIPTION@@', $target.Description).
            Replace('@@FILE_TYPE@@', $target.Type)
        [IO.File]::WriteAllText($rcPath, $resource, [Text.UTF8Encoding]::new($false))
        $objectPath = Join-Path $work "$baseName.obj"
        $binaryPath = Join-Path $output $target.Name
        $libraries = if ($target.Name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            'ole32.lib shell32.lib shlwapi.lib advapi32.lib'
        } else { 'windowsapp.lib shell32.lib shlwapi.lib' }
        $dllSwitch = if ($target.Name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) { '/LD' } else { '' }
        $exportSwitch = if ($target.Name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            '/EXPORT:DllGetClassObject /EXPORT:DllCanUnloadNow'
        } else { '' }
        $command = 'call "{0}" >nul && rc.exe /nologo /fo "{1}" "{2}" && cl.exe /nologo /std:c++20 /EHsc /O2 /GL /MT /GS /guard:cf /DUNICODE /D_UNICODE {3} /Fo:"{4}" /Fe:"{5}" "{6}" "{7}" /link /LTCG /OPT:REF /OPT:ICF /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /MACHINE:X64 {8} {9}' -f `
            $vcvars, $resPath, $rcPath, $dllSwitch, $objectPath, $binaryPath, (Join-Path $source $target.Source), $resPath, $libraries, $exportSwitch
        & $env:ComSpec /d /s /c $command
        if ($LASTEXITCODE -ne 0) { throw "Native modern context-menu build failed: $($target.Name)" }
    }
    Get-ChildItem -LiteralPath $output -File | Where-Object { $_.Extension -in @('.lib', '.exp') } | Remove-Item -Force

    $manifest = [IO.File]::ReadAllText((Join-Path $source 'AppxManifest.xml.in')).
        Replace('@@PUBLISHER@@', $Publisher).
        Replace('@@VERSION@@', $Version)
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $root 'src\Assets\Brand\SwSearch-256.png') -Destination $layout
    Copy-Item -LiteralPath (Join-Path $output 'SwSearch.ModernContextMenu.dll') -Destination $layout
    Copy-Item -LiteralPath (Join-Path $output 'SwSearch.ModernContextMenuRegistration.exe') -Destination $layout
    $msix = Join-Path $output 'SwSearch.ModernContextMenu.msix'
    & $makeAppx pack /d $layout /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw 'Sparse MSIX packaging failed.' }
    & $signTool sign /fd SHA256 /f $privatePfx /p $password $msix
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $verifyText = (& $signTool verify /pa /all /v $msix 2>&1 | Out-String)
        $verifyExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
    $signature = Get-AuthenticodeSignature -LiteralPath $msix
    $isExpectedPreTrustResult =
        $verifyExitCode -ne 0 -and
        $verifyText -match '(?is)root[\s\S]*certificate[\s\S]*not trusted' -and
        $signature.SignerCertificate -and
        $signature.SignerCertificate.Thumbprint -ceq $publicThumbprint -and
        $signature.SignerCertificate.Subject -ceq $Publisher
    if ($verifyExitCode -ne 0 -and -not $isExpectedPreTrustResult) {
        throw "MSIX signature verification failed: $verifyText"
    }
    if ($verifyExitCode -eq 0 -and
        (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $publicThumbprint)) {
        throw 'MSIX signer does not match the packaged public certificate.'
    }
}
finally {
    if ($certificate) { $certificate.Dispose() }
    $password = $null
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}

$privateLeaks = @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Extension -in @('.pfx','.p12','.pvk','.key') })
if ($privateLeaks.Count -ne 0) { throw "Private signing material leaked into output: $($privateLeaks.FullName -join ', ')" }
$required = @(
    'SwSearch.ModernContextMenu.dll',
    'SwSearch.ModernContextMenuRegistration.exe',
    'SwSearch.ModernContextMenu.msix',
    'SwSearch.ModernContextMenu.cer')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $output $name) -PathType Leaf)) { throw "Modern menu output missing: $name" }
}
Write-Output "Modern context menu: $output"
Write-Output "Publisher: $Publisher"
Write-Output "Certificate thumbprint: $publicThumbprint"
Write-Output 'SignTool verification: signature/digest present; trust is established by the MSI TrustedPeople lifecycle.'
