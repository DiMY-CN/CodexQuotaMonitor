param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.1.1'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$rootPrefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$appProject = Join-Path $root 'src\CodexQuotaMonitor.Wpf\CodexQuotaMonitor.Wpf.csproj'
$installerProject = Join-Path $root 'installer\CodexQuotaMonitor.Installer.wixproj'
$appOutput = Join-Path $root 'publish\win-x64-self-contained'
$installerOutput = Join-Path $root 'publish\installer'

function Remove-ProjectDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the project: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

foreach ($path in @($appOutput, $installerOutput)) {
    Remove-ProjectDirectory -Path $path
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

try {
    $publishArgs = @(
        'publish', $appProject,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        '-o', $appOutput
    )
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Application publish failed with exit code $LASTEXITCODE."
    }

    $installerArgs = @(
        'build', $installerProject,
        '-c', 'Release',
        "-p:ProductVersion=$Version",
        "-p:PublishDir=$appOutput\",
        '-o', $installerOutput
    )
    & dotnet @installerArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }

    $target = Join-Path $installerOutput "CodexQuotaMonitor-Setup-$Version.msi"
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Installer output was not created at the expected path: $target"
    }

    Get-ChildItem -LiteralPath $installerOutput -Filter '*.wixpdb' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}
finally {
    foreach ($path in @(
        (Join-Path $root 'src\CodexQuotaMonitor.Wpf\bin'),
        (Join-Path $root 'src\CodexQuotaMonitor.Wpf\obj'),
        (Join-Path $root 'installer\bin'),
        (Join-Path $root 'installer\obj')
    )) {
        Remove-ProjectDirectory -Path $path
    }
}

Write-Output "Application: $(Join-Path $appOutput 'CodexQuotaMonitor.Wpf.exe')"
Write-Output "Installer:   $target"
