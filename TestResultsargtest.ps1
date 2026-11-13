$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'C:\Program Files\dotnet\dotnet.exe'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.WorkingDirectory = 'C:\Users\liliy\Documents\solidworks插件'

function Run([string]$label, [string]$args) {
    $psi.Arguments = $args
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    Write-Output ("{0}: exit={1} err={2}" -f $label, $p.ExitCode, $(if ($err) { $err.Substring(0, [Math]::Min(120, $err.Length)).Trim() } else { '' }))
}

Run 'unquoted-ascii'      'exec "C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll" -property:Foo=C:\abc -version'
Run 'quoted-ascii'        'exec "C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll" -property:Foo="C:\abc" -version'
Run 'unquoted-nonascii'   'exec "C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll" -property:Foo=C:\插件\abc -version'
Run 'quoted-nonascii'     'exec "C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll" -property:Foo="C:\插件\abc" -version'
