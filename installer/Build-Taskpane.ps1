[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\taskpane'),
    [string]$SolidWorksInteropPath = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'src\SwSearch.SolidWorksAddin'
$webAssetsSource = Join-Path $root 'src\SwSearch.WebUi\Assets\webui'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw ".NET Framework 4.x x64 csc.exe not found: $csc" }
$SolidWorksInteropPath = [IO.Path]::GetFullPath($SolidWorksInteropPath)
if (-not (Test-Path -LiteralPath $SolidWorksInteropPath -PathType Leaf)) {
    throw "SOLIDWORKS 2026 official PIA not found: $SolidWorksInteropPath"
}
$webViewVersion = '1.0.4078.44'
$webViewCandidates = @(
    (Join-Path $root ".packages-rc48-r3\microsoft.web.webview2\$webViewVersion")
)
$webViewPackage = @($webViewCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1)
if ($webViewPackage.Count -ne 1) {
    throw "Cached Microsoft.Web.WebView2 $webViewVersion package not found; offline build will not download it."
}
$webViewCore = Join-Path $webViewPackage[0] 'lib\net462\Microsoft.Web.WebView2.Core.dll'
$webViewLoader = Join-Path $webViewPackage[0] 'runtimes\win-x64\native\WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $webViewCore -PathType Leaf) -or
    -not (Test-Path -LiteralPath $webViewLoader -PathType Leaf)) {
    throw 'Cached WebView2 SDK is incomplete (net462 Core or x64 Loader missing).'
}
if (-not (Test-Path -LiteralPath (Join-Path $webAssetsSource 'taskpane.html') -PathType Leaf)) {
    throw "Shared taskpane web assets not found: $webAssetsSource"
}
$interopItem = Get-Item -LiteralPath $SolidWorksInteropPath
$interopHash = (Get-FileHash -LiteralPath $SolidWorksInteropPath -Algorithm SHA256).Hash
$interopName = [Reflection.AssemblyName]::GetAssemblyName($SolidWorksInteropPath)
if ($interopItem.Length -ne 2831960 -or
    $interopHash -cne 'F3AF048BB4064B8FE7E3BB8E1B5D6E6AAB5DC78C04B3EBAA7927B3EE923F6279' -or
    $interopName.FullName -cne 'SolidWorks.Interop.sldworks, Version=34.3.2.3, Culture=neutral, PublicKeyToken=7c4797c3e4eeac03') {
    throw 'SOLIDWORKS 2026 official PIA identity does not match the reviewed build input.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$dll = Join-Path $output 'SwSearch.SolidWorksAddin.dll'
$icon = Join-Path $root 'src\Assets\Brand\SwSearch.ico'
$sources = @(
    Get-ChildItem -LiteralPath $source -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName
    Join-Path $root 'src\Shared\WindowsScopePath.cs'
)
if ($sources.Count -lt 5) { throw 'Taskpane source set is incomplete.' }
$arguments = @(
    '/nologo', '/target:library', '/platform:x64', '/optimize+', '/debug:pdbonly', '/warnaserror+',
    "/out:$dll",
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Runtime.Serialization.dll',
    '/reference:System.Web.Extensions.dll', "/reference:$webViewCore",
    "/link:$SolidWorksInteropPath",
    "/resource:$icon,SwSearch.ico"
) + $sources
& $csc $arguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Taskpane compilation failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $webViewCore -Destination (Join-Path $output 'Microsoft.Web.WebView2.Core.dll') -Force
Copy-Item -LiteralPath $webViewLoader -Destination (Join-Path $output 'WebView2Loader.dll') -Force
$webAssetsOutput = Join-Path $output 'webui'
New-Item -ItemType Directory -Force -Path $webAssetsOutput | Out-Null
Get-ChildItem -LiteralPath $webAssetsSource -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $webAssetsOutput $_.Name) -Force
}
[pscustomobject]@{
    FullName = $dll
    Length = (Get-Item -LiteralPath $dll).Length
    SHA256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    InteropInput = $SolidWorksInteropPath
    InteropSHA256 = $interopHash
    InteropAssembly = $interopName.FullName
    InteropMode = 'EmbedInteropTypes (/link); no runtime PIA payload dependency'
    WebView2Sdk = "$webViewVersion cached net462 Core + x64 Loader; system Evergreen Runtime required"
    WebAssets = $webAssetsOutput
}
