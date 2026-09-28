[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.2.0',
    [string]$DotnetPath = 'dotnet',
    [string]$InnoSetupCompiler = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [ValidatePattern('^(|10\.0\.\d+)$')]
    [string]$RuntimeFrameworkVersion = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactDirectory = Join-Path $workspaceDirectory 'artifacts'
$publishDirectory = Join-Path $artifactDirectory 'publish'
$installerDirectory = Join-Path $artifactDirectory 'installer'
$appProject = Join-Path $workspaceDirectory 'src\Timekeeper.App\Timekeeper.App.csproj'
$installerScript = Join-Path $workspaceDirectory 'installer\Timekeeper.iss'

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE. Build stopped."
    }
}

function Assert-PlainArtifactPath {
    param([string]$Path)
    $absolutePath = [IO.Path]::GetFullPath($Path)
    $allowedPrefix = [IO.Path]::GetFullPath($artifactDirectory).TrimEnd('\') + '\'
    if (-not $absolutePath.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path is outside the expected directory: $absolutePath"
    }
    # Reject junctions and symbolic links before deleting an old publish output.
    foreach ($candidate in @($artifactDirectory, $absolutePath)) {
        if (Test-Path -LiteralPath $candidate) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Artifact directories must not be links: $candidate"
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "Application project not found: $appProject"
}
if (-not (Get-Command $DotnetPath -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK, or supply -DotnetPath with its executable path.'
}
if (-not (Test-Path -LiteralPath $InnoSetupCompiler -PathType Leaf)) {
    throw 'Install Inno Setup 6, or supply -InnoSetupCompiler with the path to ISCC.exe.'
}

Push-Location -LiteralPath $workspaceDirectory
try {
    foreach ($testProject in @('tests\Timekeeper.Tests\Timekeeper.Tests.csproj', 'tests\Timekeeper.ApiTests\Timekeeper.ApiTests.csproj', 'tests\Timekeeper.UpdateTests\Timekeeper.UpdateTests.csproj')) {
        if (Test-Path -LiteralPath $testProject -PathType Leaf) {
            Write-Host "Running $testProject"
            Invoke-Checked $DotnetPath @('run', '--project', $testProject, '--configuration', $Configuration)
        }
    }

    Assert-PlainArtifactPath $publishDirectory
    Assert-PlainArtifactPath $installerDirectory
    if (Test-Path -LiteralPath $publishDirectory) {
        # Only this verified generated output directory is replaced.
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

    $publishArguments = @('publish', $appProject, '--configuration', $Configuration,
        '--runtime', 'win-x64', '--self-contained', 'true', '--output', $publishDirectory,
        "-p:Version=$Version", '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($RuntimeFrameworkVersion) {
        $publishArguments += "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion"
    }
    Invoke-Checked $DotnetPath $publishArguments
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'Timekeeper.exe') -PathType Leaf)) {
        throw 'Publish completed without Timekeeper.exe. Installer compilation stopped.'
    }

    Invoke-Checked $InnoSetupCompiler @("/DAppVersion=$Version", "/DPublishDir=$publishDirectory",
        "/DInstallerOutputDir=$installerDirectory", $installerScript)
    $setupPath = Join-Path $installerDirectory "Timekeeper-Setup-$Version-win-x64.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Installer output not found: $setupPath"
    }
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($setupPath))" | Set-Content -LiteralPath (Join-Path $installerDirectory 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Installer ready: $setupPath"
    Write-Host 'Unsigned local build. The installer was compiled, not installed or launched.'
}
finally {
    Pop-Location
}
