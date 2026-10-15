#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$PackageDirectory = $PSScriptRoot, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
Start-Transcript -Path (Join-Path $PackageDirectory 'registration.log') -Force | Out-Null
trap { Write-Error $_ -ErrorAction Continue; Stop-Transcript | Out-Null; exit 1 }
$id = '{E2CF72C9-B695-48DF-968F-EAB0E85B9A26}'
$hostDll = Join-Path $PackageDirectory 'addin\SolidWorksAgent.ChatAddin.V5.comhost.dll'
$key = "HKLM:\SOFTWARE\SolidWorks\Addins\$id"
$startup = "HKCU:\SOFTWARE\SolidWorks\AddInsStartup\$id"
if (-not (Test-Path $hostDll)) { throw "Package not found: $hostDll. Run Build-ChatAddin.ps1 first, then run the script in artifacts\chat-addin." }
if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell.' }
# Registration does not load/unload the add-in or close any SolidWorks documents.
$regsvr = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
if ($Uninstall) {
    $registration = Get-Item "Registry::HKEY_CLASSES_ROOT\CLSID\$id\InprocServer32" -ErrorAction SilentlyContinue
    $registered = if ($registration) { $registration.GetValue('') } else { $null }
    if ($registered -and $registered -ne $hostDll) { throw "A different package is registered: $registered" }
    $p = Start-Process $regsvr -ArgumentList @('/s', '/u', ('"' + $hostDll + '"')) -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "COM unregistration failed: $($p.ExitCode)" }
    Remove-Item $key -Recurse -ErrorAction SilentlyContinue
    Remove-Item $startup -Recurse -ErrorAction SilentlyContinue
    Write-Output 'Unregistered. CAD files and chat settings were preserved.'
    exit
}
$p = Start-Process $regsvr -ArgumentList @('/s', ('"' + $hostDll + '"')) -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "COM registration failed: $($p.ExitCode). Install the x64 .NET 8 Desktop Runtime." }
$addinKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("SOFTWARE\SolidWorks\Addins\$id")
try {
    $addinKey.SetValue('', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
    $addinKey.SetValue('Title', 'SolidWorks AI Chat')
    $addinKey.SetValue('Description', 'Chat with Codex, Claude or Gemini using SolidWorksAgent.')
} finally { $addinKey.Dispose() }
$startupKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("SOFTWARE\SolidWorks\AddInsStartup\$id")
try { $startupKey.SetValue('', 1, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $startupKey.Dispose() }
Write-Output 'Registered. In SOLIDWORKS, open Tools > Add-Ins and enable SolidWorks AI Chat.'
Write-Output ('In Chat Settings, select swagent.exe: ' + (Join-Path $PackageDirectory 'cli\swagent.exe'))
Stop-Transcript | Out-Null
