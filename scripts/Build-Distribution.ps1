[CmdletBinding()]
param([string]$OutputDirectory, [string]$SolidWorksInteropDirectory, [string]$SourceArchive, [string]$SourceCommit)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($SourceArchive) {
    if ($SourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Supply the full source commit SHA with SourceArchive.' }
    $commit = $SourceCommit
} else {
    $commit = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not read Git HEAD.' }
}
$id = 'solidworks-ai-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + $commit.Substring(0,7)
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root ('artifacts\releases\' + $id) }
if (Test-Path $output) { throw 'Use a new output directory.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$stage = Join-Path $output 'build'
$package = Join-Path $output 'SolidWorksAI'
New-Item -ItemType Directory -Path $stage,$package -Force | Out-Null
# Export an explicit allowlist from the committed revision, never the working CAD workspace.
$archive = Join-Path $output 'source.zip'
if ($SourceArchive) { Copy-Item -LiteralPath $SourceArchive -Destination $archive }
else {
    & git -C $root archive --format=zip "--output=$archive" $commit -- src tests AGENTS.md CAD_MODEL_RULES.md DRAWING_RULES.md Directory.Build.props SolidWorksAgent.sln .gitignore scripts/Build-ChatAddin.ps1 scripts/Register-ChatAddin.ps1 docs/api-automation.md docs/architecture.md docs/chat-addin.md
    if ($LASTEXITCODE -ne 0) { throw 'Source export failed.' }
}
Expand-Archive -LiteralPath $archive -DestinationPath $stage
$build = @{OutputDirectory=(Join-Path $package 'app')}
if ($SolidWorksInteropDirectory) { $build.SolidWorksInteropDirectory=$SolidWorksInteropDirectory }
& (Join-Path $stage 'scripts\Build-ChatAddin.ps1') @build
if (-not (Test-Path (Join-Path $package 'app\addin\SolidWorksAgent.ChatAddin.V7.comhost.dll'))) { throw 'Add-in build did not complete.' }
# Ship no vendor runtime, debugging paths, personal defaults or build logs.
Get-ChildItem (Join-Path $package 'app') -Recurse -File | Where-Object { $_.Name -like 'SolidWorks.Interop.*' -or $_.Extension -in @('.pdb','.log') -or $_.Name -eq 'chat-defaults.json' } | Remove-Item
Get-ChildItem (Join-Path $package 'app') -File | Remove-Item
$project = Join-Path $package 'project'
New-Item -ItemType Directory -Path $project | Out-Null
# Extract the pristine archive again instead of copying the now-built tree.
Expand-Archive -LiteralPath $archive -DestinationPath $project
Copy-Item (Join-Path $PSScriptRoot 'distribution\*') $package
Copy-Item (Join-Path $root 'docs\distribution-guide.txt') (Join-Path $package 'START-HERE.txt')
# Replace the historical developer-specific example path in the copied documentation only.
$doc = Join-Path $project 'docs\chat-addin.md'
$text = [IO.File]::ReadAllText($doc).Replace('C:\Users\takea\Documents\Projects\Solidworks_AI','C:\path\SolidWorksAgent')
[IO.File]::WriteAllText($doc,$text,(New-Object Text.UTF8Encoding($false)))
$files = @(Get-ChildItem $package -File -Recurse | ForEach-Object {
    [ordered]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
})
[ordered]@{id=$id;sourceCommit=$commit;testedSolidWorks='2026 SP3 x64';framework='.NET 8 Desktop Runtime x64';files=$files} | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $package 'package.json')
$zip = Join-Path $output ($id + '.zip')
Compress-Archive -LiteralPath $package -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText($zip + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($zip))
Write-Output "Distribution ZIP: $zip"
Write-Output "SHA256: $hash"
Write-Output 'Built from committed app source. Installation scripts are from the current packaging work.'
