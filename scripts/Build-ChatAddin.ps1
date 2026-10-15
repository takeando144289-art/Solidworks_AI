[CmdletBinding()]
param([string]$SolidWorksInteropDirectory, [string]$Configuration = 'Release', [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $SolidWorksInteropDirectory) {
    $candidates = @(
        "$env:ProgramFiles\SOLIDWORKS Corp\SOLIDWORKS\api\redist",
        "$env:ProgramFiles\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist"
    )
    $SolidWorksInteropDirectory = $candidates | Where-Object { Test-Path (Join-Path $_ 'SolidWorks.Interop.swpublished.dll') } | Select-Object -First 1
}
if (-not $SolidWorksInteropDirectory) { throw 'Specify -SolidWorksInteropDirectory pointing to SOLIDWORKS api\redist.' }
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$package = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts\chat-addin' }
New-Item -ItemType Directory -Path $package -Force | Out-Null
function Invoke-DotNet([string[]]$Arguments, [string]$Name) {
    # Start-Process also waits correctly when invoked from a non-console host.
    $quoted = ($Arguments | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }) -join ' '
    $stdout = Join-Path $package "$Name-build.log"
    $stderr = Join-Path $package "$Name-build-errors.log"
    # Wait for the CLI itself, not persistent MSBuild/compiler server descendants.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $dotnet; $info.Arguments = $quoted; $info.WorkingDirectory = $root
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8; $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($info)
    $outputTask = $p.StandardOutput.ReadToEndAsync(); $errorTask = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [IO.File]::WriteAllText($stdout, $outputTask.Result)
    [IO.File]::WriteAllText($stderr, $errorTask.Result)
    Get-Content $stdout -Encoding UTF8 | Write-Output
    Get-Content $stderr -Encoding UTF8 | Write-Output
    if ($p.ExitCode -ne 0) { throw "$Name build failed: $($p.ExitCode)" }
}
Invoke-DotNet @('publish', (Join-Path $root 'src\SolidWorksAgent.Tools\SolidWorksAgent.Tools.csproj'), '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '-o', (Join-Path $package 'cli')) 'CLI'
Invoke-DotNet @('publish', (Join-Path $root 'src\SolidWorksAgent.SwApi\Addin\SolidWorksAgent.Addin.csproj'), '-c', $Configuration, '--self-contained', 'false', "-p:SolidWorksInteropDirectory=$SolidWorksInteropDirectory", '-o', (Join-Path $package 'addin')) 'Addin'
@{ projectDirectory = $root; cadExecutable = (Join-Path $package 'cli\swagent.exe') } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $package 'addin\chat-defaults.json')
Copy-Item (Join-Path $PSScriptRoot 'Register-ChatAddin.ps1') $package
Copy-Item (Join-Path $root 'docs\chat-addin.md') $package
Copy-Item (Join-Path $root 'docs\api-automation.md') $package
Write-Output "Package ready: $package"
Write-Output 'Register using elevated Windows PowerShell: .\Register-ChatAddin.ps1'
