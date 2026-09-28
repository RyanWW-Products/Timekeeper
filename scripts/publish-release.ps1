[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$NotesFile
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = 'RyanWW-Products/Timekeeper'
$notesPath = (Resolve-Path -LiteralPath $NotesFile).Path
Push-Location -LiteralPath $workspaceDirectory
try {
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the intended source changes before publishing.' }
    $tagCommit = git rev-parse "v$Version^{commit}"
    if ($LASTEXITCODE -ne 0) { throw 'Create and push the version tag before publishing.' }
    $headCommit = git rev-parse HEAD
    if ($tagCommit -ne $headCommit) { throw 'Check out the version tag before building its release.' }
    & (Join-Path $PSScriptRoot 'build.ps1') -Version $Version
    $smokeDirectory = Join-Path $workspaceDirectory 'artifacts/release-ui-check'
    $appPath = Join-Path $workspaceDirectory 'artifacts/publish/Timekeeper.exe'
    $process = Start-Process -FilePath $appPath -ArgumentList @('--smoke-test', ('"' + $smokeDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Published UI verification failed.' }
    $setup = Join-Path $workspaceDirectory "artifacts/installer/Timekeeper-Setup-$Version-win-x64.exe"
    $checksum = Join-Path $workspaceDirectory 'artifacts/installer/SHA256SUMS.txt'
    # Upload to a draft first; it cannot be offered by the updater until published.
    gh release create "v$Version" $setup $checksum --repo $repository --draft --verify-tag --title "Timekeeper $Version" --notes-file $notesPath
    if ($LASTEXITCODE -ne 0) { throw 'Release upload failed. Inspect GitHub before retrying.' }
    gh release edit "v$Version" --repo $repository --draft=false --latest
    if ($LASTEXITCODE -ne 0) { throw 'The draft was uploaded but not published. Inspect GitHub.' }
}
finally { Pop-Location }
