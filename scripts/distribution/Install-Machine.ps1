#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory, [Parameter(Mandatory=$true)][string]$InteropDirectory)
$ErrorActionPreference = 'Stop'
try {
    if (-not [Environment]::Is64BitProcess) { throw '64-bit Windows PowerShell is required.' }
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS before installation.' }
    $manifest = Get-Content (Join-Path $PackageDirectory 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.id -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid package ID.' }
    $target = Join-Path $env:ProgramFiles ('SolidWorksAgent\releases\' + $manifest.id)
    $source = Join-Path $PackageDirectory 'app'
    foreach ($file in $manifest.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $file.path))
        if (-not $path.StartsWith([IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid manifest path.' }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package checksum mismatch: $($file.path)" }
    }
    foreach ($name in @('sldworks','swpublished','swconst')) {
        if (-not (Test-Path (Join-Path $InteropDirectory "SolidWorks.Interop.$name.dll"))) { throw "Missing SOLIDWORKS interop: $name" }
    }
    if (Test-Path $target) {
        if (-not (Test-Path (Join-Path $target 'installed.json'))) { throw 'Installation directory already exists without an install marker. It was left unchanged.' }
        # Do not overwrite registered DLLs even when retrying a setup.
        foreach ($file in @($manifest.files | Where-Object path -like 'app/*')) {
            $relative = $file.path.Substring(4)
            if ((Get-FileHash -LiteralPath (Join-Path $target $relative)).Hash -ne $file.sha256) { throw 'Installed files differ. Build a new release rather than overwriting them.' }
        }
    } else {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Copy-Item (Join-Path $source '*') $target -Recurse
        foreach ($folder in @('addin','cli')) {
            foreach ($name in @('sldworks','swpublished','swconst')) {
                Copy-Item (Join-Path $InteropDirectory "SolidWorks.Interop.$name.dll") (Join-Path $target $folder)
            }
        }
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $target 'installed.json')
    }
    $id = '{E2CF72C9-B695-48DF-968F-EAB0E85B9A28}'
    $hostDll = Join-Path $target 'addin\SolidWorksAgent.ChatAddin.V7.comhost.dll'
    $previousKey = Get-Item "Registry::HKEY_CLASSES_ROOT\CLSID\$id\InprocServer32" -ErrorAction SilentlyContinue
    $previous = if ($previousKey) { $previousKey.GetValue('') } else { $null }
    $regsvr = "$env:SystemRoot\System32\regsvr32.exe"
    $process = Start-Process $regsvr -ArgumentList @('/s', ('"' + $hostDll + '"')) -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        if ($previous -and (Test-Path -LiteralPath $previous)) { Start-Process $regsvr -ArgumentList @('/s', ('"' + $previous + '"')) -Wait }
        throw "COM registration failed ($($process.ExitCode)). Check .NET 8 Desktop Runtime x64."
    }
    $key = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("SOFTWARE\SolidWorks\Addins\$id")
    try {
        $key.SetValue('',0,[Microsoft.Win32.RegistryValueKind]::DWord)
        $key.SetValue('Title','SolidWorks AI Chat')
        $key.SetValue('Description','Chat with Codex, Claude or Gemini using SolidWorksAgent.')
    } finally { $key.Dispose() }
    exit 0
} catch { Write-Error $_ -ErrorAction Continue; Read-Host 'Press Enter to close'; exit 1 }
