[CmdletBinding()]
param([switch]$CheckOnly, [switch]$LoginOnly, [string]$SolidWorksInteropDirectory)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$package = $PSScriptRoot
$profileRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SolidWorksAgent'
$settingsPath = Join-Path $profileRoot 'chat-settings.json'
function Run([string]$exe, [string[]]$arguments) {
    # Array invocation preserves spaces and metacharacters; never assemble a command string.
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $exe" }
}
function Read-DotNet([string]$argument) {
    # Capture explicitly: native output can be lost when PowerShell is launched from a GUI/WSL host.
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $dotnet; $info.Arguments = $argument
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8; $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    $process = [Diagnostics.Process]::Start($info)
    $outputTask = $process.StandardOutput.ReadToEndAsync(); $errorTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ('Could not inspect .NET: ' + $errorTask.Result) }
    return $outputTask.Result -split '\r?\n'
}
function Find-Interop {
    if ($SolidWorksInteropDirectory) { $candidates = @($SolidWorksInteropDirectory) }
    else {
        $candidates = @("$env:ProgramFiles\SOLIDWORKS Corp\SOLIDWORKS\api\redist")
        foreach ($folder in @("$env:ProgramFiles\Dassault Systemes", "$env:ProgramFiles\SOLIDWORKS Corp")) {
            if (Test-Path -LiteralPath $folder) {
                $candidates += @(Get-ChildItem -LiteralPath $folder -Filter 'SolidWorks.Interop.swpublished.dll' -Recurse -ErrorAction SilentlyContinue | ForEach-Object DirectoryName)
            }
        }
        foreach ($key in @(Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue | Where-Object PSChildName -match '^SOLIDWORKS 20')) {
            $install = (Get-ItemProperty ($key.PSPath + '\Setup') -ErrorAction SilentlyContinue).'SolidWorks Folder'
            if ($install) { $candidates += Join-Path $install 'api\redist' }
        }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ((Test-Path (Join-Path $candidate 'SolidWorks.Interop.swpublished.dll')) -and
            (Test-Path (Join-Path $candidate 'SolidWorks.Interop.sldworks.dll')) -and
            (Test-Path (Join-Path $candidate 'SolidWorks.Interop.swconst.dll'))) { return $candidate }
    }
    throw 'SOLIDWORKS API files not found. Install SOLIDWORKS first, or run Setup.ps1 -SolidWorksInteropDirectory <api\redist>.'
}
function Find-Codex {
    $app = Get-AppxPackage 'OpenAI.Codex' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($app) {
        $exe = Join-Path $app.InstallLocation 'app\resources\codex.exe'
        if (Test-Path -LiteralPath $exe) { return $exe }
    }
    $candidates = @("$env:USERPROFILE\.local\bin\codex.exe",
        (Join-Path $profileRoot 'tools\node_modules\@openai\codex\bin\codex.js'),
        "$env:APPDATA\npm\node_modules\@openai\codex\bin\codex.js")
    $command = Get-Command codex.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    return $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
function Find-Node {
    $command = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $path = "$env:ProgramFiles\nodejs\node.exe"
    if (Test-Path $path) { return $path }
    return $null
}
function Install-Winget([string]$id) {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { throw "Install $id from its official website, then rerun Setup. Windows App Installer (winget) was not found." }
    Run $winget.Source @('install', '--id', $id, '--exact', '--source', 'winget', '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')
}
function Login-Codex {
    if (-not (Test-Path $settingsPath)) { throw 'Run Setup.cmd first.' }
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $exe = $settings.providers.Codex.executable
    Write-Host 'Sign in with YOUR ChatGPT account in the browser. Credentials are stored by Codex, not in this package.'
    if ([IO.Path]::GetExtension($exe) -in @('.js','.mjs')) { Run $settings.nodeExecutable @($exe, 'login') }
    else { Run $exe @('login') }
}
try {
    if (-not [Environment]::Is64BitProcess) { throw 'Run Setup.cmd with 64-bit Windows PowerShell.' }
    if ($LoginOnly) { Login-Codex; exit 0 }
    $manifest = Get-Content (Join-Path $package 'package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.id -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid package ID.' }
    $interop = Find-Interop
    $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
    $codex = Find-Codex
    $node = Find-Node
    $runtime = $false; $sdk = $false
    if (Test-Path $dotnet) {
        $runtime = [bool]((Read-DotNet '--list-runtimes') -match '^Microsoft.WindowsDesktop.App 8\.')
        $sdk = [bool]((Read-DotNet '--list-sdks') -match '^8\.')
    }
    $machineRoot = Join-Path $env:ProgramFiles ("SolidWorksAgent\releases\" + $manifest.id)
    $projectRoot = Join-Path $profileRoot ("Projects\" + $manifest.id)
    # CheckOnly is read-only: no downloads, registry writes, login, or settings changes.
    if ($CheckOnly) {
        [ordered]@{ package=$manifest.id; interop=$interop; desktopRuntime8=$runtime; sdk8=$sdk; codex=$codex; node=$node; installDirectory=$machineRoot; projectDirectory=$projectRoot; solidWorksRunning=[bool](Get-Process SLDWORKS -ErrorAction SilentlyContinue) } | ConvertTo-Json
        exit 0
    }
    if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Save your work and close SOLIDWORKS, then rerun Setup.cmd.' }
    Write-Host 'Setup installs the app and, if missing, .NET 8 SDK/Desktop Runtime and the Windows Codex CLI.'
    Write-Host 'Your CAD files, AI credentials and WSL settings will not be changed. Internet access and administrator approval may be required.'
    if (-not $sdk) { Install-Winget 'Microsoft.DotNet.SDK.8' }
    if (-not $runtime -and -not [bool]((Read-DotNet '--list-runtimes') -match '^Microsoft.WindowsDesktop.App 8\.')) { Install-Winget 'Microsoft.DotNet.DesktopRuntime.8' }
    if (-not $codex) {
        if (-not $node) { Install-Winget 'OpenJS.NodeJS.LTS'; $node = Find-Node }
        if (-not $node) { throw 'Node.js installation could not be detected. Reopen Setup.cmd.' }
        $npm = Join-Path (Split-Path $node) 'node_modules\npm\bin\npm-cli.js'
        if (-not (Test-Path $npm)) { throw 'npm was not found beside Node.js. Install the Windows Codex CLI, then rerun Setup.' }
        Run $node @($npm, 'install', '--prefix', (Join-Path $profileRoot 'tools'), '--no-audit', '--no-fund', '@openai/codex')
        $codex = Find-Codex
        if (-not $codex) { throw 'Codex installation could not be detected.' }
    }
    if ([IO.Path]::GetExtension($codex) -in @('.js','.mjs') -and -not $node) { Install-Winget 'OpenJS.NodeJS.LTS'; $node = Find-Node; if (-not $node) { throw 'Node.js was not detected. Reopen Setup.cmd.' } }
    # The only elevated step installs/registers machine binaries. User settings stay in this process.
    $helper = Join-Path $package 'Install-Machine.ps1'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $helper + '" -PackageDirectory "' + $package + '" -InteropDirectory "' + $interop + '"'
    $registration = Start-Process "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    if ($registration.ExitCode -ne 0) { throw 'Machine installation failed or was cancelled. Your chat settings have not been replaced.' }
    # A fresh project per release avoids overwriting anyone's source edits or CAD output.
    if (-not (Test-Path $projectRoot)) {
        New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
        Copy-Item (Join-Path $package 'project\*') $projectRoot -Recurse
    }
    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    if (Test-Path $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Copy-Item -LiteralPath $settingsPath -Destination ($settingsPath + '.backup-' + (Get-Date -Format yyyyMMdd-HHmmss))
        # Preserve model/provider preferences and an existing valid working directory.
        if (-not (Test-Path -LiteralPath $settings.projectDirectory -PathType Container)) { $settings.projectDirectory = $projectRoot }
        $settings.cadExecutable = Join-Path $machineRoot 'cli\swagent.exe'
        $settings.nodeExecutable = $(if ($node) { $node } else { 'node.exe' })
        if (-not (Test-Path -LiteralPath $settings.providers.Codex.executable -PathType Leaf)) { $settings.providers.Codex.executable = $codex }
    } else {
        $settings = [ordered]@{ projectDirectory=$projectRoot; cadExecutable=(Join-Path $machineRoot 'cli\swagent.exe'); nodeExecutable=$(if ($node) { $node } else { 'node.exe' }); provider=0; providers=[ordered]@{
            Codex=@{executable=$codex;model=''}; Claude=@{executable='claude.exe';model=''}; Gemini=@{executable='gemini.exe';model=''} } }
    }
    $json = $settings | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($settingsPath + '.tmp', $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath ($settingsPath + '.tmp') -Destination $settingsPath -Force
    $startup = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('SOFTWARE\SolidWorks\AddInsStartup\{E2CF72C9-B695-48DF-968F-EAB0E85B9A28}')
    try { $startup.SetValue('',1,[Microsoft.Win32.RegistryValueKind]::DWord) } finally { $startup.Dispose() }
    Write-Host 'Installation complete. Run Login-Codex.cmd, then start SOLIDWORKS.'
    Write-Host "Project: $projectRoot"
    Write-Host 'If the pane is hidden: Tools > Add-Ins > SolidWorks AI Chat.'
} catch { Write-Error $_ -ErrorAction Continue; exit 1 }
