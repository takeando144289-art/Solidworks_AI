[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$manifest = Get-Content (Join-Path $PackageDirectory 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($item in $manifest.files) {
    $file = Join-Path $PackageDirectory $item.path
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.sha256) { throw "Checksum mismatch: $($item.path)" }
    if ($item.path -match '(^|/)(workspace|output|logs|\.codex|\.git|node_modules|bin|obj)/' -or $item.path -match '(auth\.json|chat-settings\.json|chat-defaults\.json|\.pdb$|SolidWorks\.Interop\.)') { throw "Unexpected distribution file: $($item.path)" }
}
foreach ($name in @('Setup.cmd','Setup.ps1','Login-Codex.cmd','Install-Machine.ps1','START-HERE.txt','app/cli/swagent.exe','app/addin/SolidWorksAgent.ChatAddin.V7.comhost.dll','project/AGENTS.md','project/CAD_MODEL_RULES.md','project/DRAWING_RULES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory $name))) { throw "Missing distribution file: $name" }
}
foreach ($file in (Get-ChildItem $PackageDirectory -Filter '*.ps1' -Recurse)) {
    $tokens=$null; $errors=$null
    [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
}
$settings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SolidWorksAgent\chat-settings.json'
$before = if (Test-Path $settings) { (Get-FileHash $settings).Hash } else { '' }
$id='{E2CF72C9-B695-48DF-968F-EAB0E85B9A28}'
$key = Get-Item "Registry::HKEY_CLASSES_ROOT\CLSID\$id\InprocServer32" -ErrorAction SilentlyContinue
$registration = if ($key) { $key.GetValue('') } else { '' }
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $PackageDirectory 'Setup.ps1') + '" -CheckOnly'
$info.UseShellExecute=$false; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
$info.StandardOutputEncoding=[Text.Encoding]::UTF8; $info.StandardErrorEncoding=[Text.Encoding]::UTF8
$p=[Diagnostics.Process]::Start($info); $o=$p.StandardOutput.ReadToEndAsync(); $e=$p.StandardError.ReadToEndAsync(); $p.WaitForExit()
if ($p.ExitCode -ne 0) { throw $e.Result }
$result=$o.Result | ConvertFrom-Json
if ($result.package -ne $manifest.id -or -not $result.interop) { throw 'Environment diagnosis did not detect the package/SOLIDWORKS.' }
$after = if (Test-Path $settings) { (Get-FileHash $settings).Hash } else { '' }
$key = Get-Item "Registry::HKEY_CLASSES_ROOT\CLSID\$id\InprocServer32" -ErrorAction SilentlyContinue
$afterRegistration = if ($key) { $key.GetValue('') } else { '' }
if ($before -ne $after -or $registration -ne $afterRegistration) { throw 'Read-only diagnosis changed existing settings or registration.' }
Write-Output ('PASS distribution manifest, file allowlist, PowerShell syntax, and read-only environment diagnosis (' + $manifest.files.Count + ' files)')
$result | ConvertTo-Json
