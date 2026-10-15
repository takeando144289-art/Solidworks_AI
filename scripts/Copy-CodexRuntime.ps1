[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Destination, [string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
if (-not $SourceDirectory) {
    $app = Get-AppxPackage 'OpenAI.Codex' | Select-Object -First 1
    if (-not $app) { throw 'Specify the Windows Codex runtime directory containing codex.exe and its companion executables.' }
    $SourceDirectory = Join-Path $app.InstallLocation 'app\resources'
}
$required = @('codex.exe', 'codex-code-mode-host.exe', 'codex-command-runner.exe', 'codex-windows-sandbox-service.exe', 'codex-windows-sandbox-setup.exe', 'rg.exe')
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $SourceDirectory $file))) { throw "Incomplete Codex runtime: $file is missing from $SourceDirectory" }
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$manifest = foreach ($file in $required) {
    $source = Join-Path $SourceDirectory $file; $target = Join-Path $Destination $file
    # Use a new package directory, never replace a runtime used by a live agent.
    if (Test-Path $target) { throw "Destination already contains $file. Use a new package directory." }
    Copy-Item -LiteralPath $source -Destination $target
    [PSCustomObject]@{ name=$file; sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Destination 'runtime-manifest.json')
Write-Output "Complete Windows Codex runtime copied to $Destination"
