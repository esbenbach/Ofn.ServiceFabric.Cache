#Requires -Version 7.0
<#
.SYNOPSIS
    Generic Service Fabric application packager that works with any Service Fabric application.

.DESCRIPTION
    Builds and assembles a Service Fabric application package by reading project structure
    from an .sfproj file and package information from ApplicationManifest.xml.

    The script automatically:
      1. Parses the .sfproj file to discover all ProjectReference items
      2. Reads ApplicationManifest.xml to get service manifest names (package names)
      3. Matches projects to packages based on naming conventions
      4. Builds each service using 'dotnet publish'
      5. Assembles the complete Service Fabric package structure

    For each discovered service the script:
      1. Runs 'dotnet publish' targeting the specified RID (defaults to win-x64)
      2. Copies the publish output into <PackageName>/Code/
      3. Copies PackageRoot/Config/ into <PackageName>/Config/ (when it exists)
      4. Copies PackageRoot/ServiceManifest.xml into <PackageName>/

    Finally it copies ApplicationManifest.xml into the package root.

.PARAMETER SfProjPath
    Path to the .sfproj file. This file will be parsed to discover project references
    and the location of ApplicationManifest.xml.

.PARAMETER Configuration
    Build configuration. Accepted values: Debug, Release. Defaults to 'Release'.

.PARAMETER RuntimeIdentifier
    RID passed to 'dotnet publish'. Defaults to 'win-x64' because Service Fabric
    clusters typically run on Windows.

.PARAMETER TargetFramework
    Target framework moniker passed to 'dotnet publish'. Defaults to 'net9.0'.

.PARAMETER OutputPath
    Root output directory for the assembled package. If not specified, defaults to
    '<SfProjDirectory>/pkg/<ApplicationTypeName>', where ApplicationTypeName is read
    from ApplicationManifest.xml. Using the type name as the folder ensures the image
    store key (which sfctl derives from the leaf folder name) is unique per application
    type and consistent with what Deploy-SFApp.ps1 expects by default.

.PARAMETER Clean
    When specified, the output directory is deleted before assembly begins.

.EXAMPLE
    # Package using the NexusApp.sfproj file
    .\Scripts\Package-SFApp.ps1 -SfProjPath .\NexusApp.sfproj

.EXAMPLE
    # Package with Debug configuration and clean build
    .\Scripts\Package-SFApp.ps1 -SfProjPath .\MyApp.sfproj -Configuration Debug -Clean

.EXAMPLE
    # Package then deploy
    $pkg = .\Scripts\Package-SFApp.ps1 -SfProjPath .\MyApp.sfproj -Clean
    .\Scripts\Deploy-NexusApp.ps1 -Environment Dev -PackagePath $pkg

.NOTES
    Package names are read directly from each project's PackageRoot/ServiceManifest.xml
    (the Name attribute on the root ServiceManifest element), so no naming conventions
    or heuristics are required.
#>

[CmdletBinding()]
param (
    [Parameter(Mandatory)]
    [string]$SfProjPath,

    [Parameter()]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [Parameter()]
    [string]$RuntimeIdentifier = 'win-x64',

    [Parameter()]
    [string]$TargetFramework = 'net10.0',

    [Parameter()]
    [string]$OutputPath,

    [Parameter()]
    [switch]$Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'SFApp.Common.psm1') -Force

# ── Validate inputs ────────────────────────────────────────────────────────────────────────────────

# Resolve-SfProjectRoot validates the path and returns the parent folder.
$projectRoot = Resolve-SfProjectRoot -SfProjPath $SfProjPath
$sfProjPath  = (Resolve-Path $SfProjPath).Path   # absolute path needed for XML parsing + display

# OutputPath default is set after parsing ApplicationManifest.xml (see below).

# ── Verify dotnet CLI is available ────────────────────────────────────────────

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "'dotnet' not found on PATH. Install the .NET SDK from https://dot.net"
}

# ── Parse .sfproj file ────────────────────────────────────────────────────────

Write-Host "Parsing Service Fabric project file: $sfProjPath"

try {
    $sfProjXml = [xml](Get-Content $sfProjPath)
    $namespace = @{ msb = 'http://schemas.microsoft.com/developer/msbuild/2003' }
    
    # Find all ProjectReference items; ensure result is always an array so .Count is safe
    $projectRefs = @( $sfProjXml | Select-Xml -XPath '//msb:ProjectReference' -Namespace $namespace |
                      ForEach-Object { $_.Node.GetAttribute('Include') } )

    if ($projectRefs.Count -eq 0) {
        throw "No ProjectReference items found in '$SfProjPath'"
    }

    Write-Host "Found $($projectRefs.Count) project references:"
    $projectRefs | ForEach-Object { Write-Host "  - $_" }
    
    # Find ApplicationManifest.xml path — try <None>, then <Content>, then filesystem scan
    $appManifestNode = $sfProjXml |
        Select-Xml -XPath '//*[contains(@Include, "ApplicationManifest.xml")]' -Namespace $namespace |
        Select-Object -First 1

    if ($appManifestNode) {
        $appManifestRelativePath = $appManifestNode.Node.GetAttribute('Include')
        $appManifestPath = Join-Path $projectRoot $appManifestRelativePath
    }
    else {
        # Not referenced in the project file — locate it on disk
        Write-Host "  ApplicationManifest.xml not referenced in .sfproj; searching project directory..."
        $found = Get-ChildItem -Path $projectRoot -Filter 'ApplicationManifest.xml' -Recurse -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if (-not $found) {
            throw "ApplicationManifest.xml not found in '$projectRoot' or its subdirectories"
        }
        $appManifestPath = $found.FullName
        $appManifestRelativePath = $found.FullName.Substring($projectRoot.Length).TrimStart('\','/')
    }

    if (-not (Test-Path $appManifestPath)) {
        throw "ApplicationManifest.xml not found at '$appManifestPath'"
    }

    Write-Host "Application manifest: $appManifestRelativePath"
}
catch {
    throw "Failed to parse .sfproj file '$SfProjPath': $($_.Exception.Message)"
}

# ── Parse ApplicationManifest.xml → application type name ─────────────────────
# The type name becomes the default output folder so that:
#   a) The image store key (= leaf folder name sfctl upload uses) is unique per
#      application type, avoiding collisions on shared clusters.
#   b) Deploy-SFApp.ps1 can locate the package without knowing the configuration.

$appTypeName = (Get-SfManifestInfo -ProjectRoot $projectRoot).TypeName

if (-not $OutputPath) {
    $OutputPath = Join-Path $projectRoot 'pkg' $appTypeName
}

# ── Resolve project paths and read package names from ServiceManifest.xml ─────

Write-Host "Resolving project service manifests..."

$services = foreach ($projectRef in $projectRefs) {
    $projectPath = if ([System.IO.Path]::IsPathRooted($projectRef)) {
        $projectRef
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $projectRoot $projectRef))
    }

    $projectDir  = Split-Path $projectPath -Parent
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)

    $serviceManifestPath = Join-Path $projectDir 'PackageRoot' 'ServiceManifest.xml'
    if (-not (Test-Path $serviceManifestPath)) {
        throw "ServiceManifest.xml not found at '$serviceManifestPath' for project '$projectName'"
    }

    $svcManifestXml = [xml](Get-Content $serviceManifestPath)
    $packageName = $svcManifestXml.ServiceManifest.Name
    if (-not $packageName) {
        throw "ServiceManifest.xml at '$serviceManifestPath' has no Name attribute on the root element"
    }

    Write-Host "  $projectName → $packageName"

    [PSCustomObject]@{
        ProjectPath = $projectPath
        ProjectDir  = $projectDir
        ProjectName = $projectName
        PackageName = $packageName
    }
}

if (-not $services) {
    throw "No services could be resolved from project references"
}

# ── Print summary ─────────────────────────────────────────────────────────────

Write-Host ('=' * 72)
Write-Host '  Service Fabric Application — Build & Package'
Write-Host ('=' * 72)
Write-Host "  SF Project    : $(Split-Path $sfProjPath -Leaf)"
Write-Host "  App type      : $appTypeName"
Write-Host "  Configuration : $Configuration"
Write-Host "  RID           : $RuntimeIdentifier"
Write-Host "  Framework     : $TargetFramework"
Write-Host "  Output        : $OutputPath"
Write-Host "  Services      : $($services.Count)"
Write-Host ('-' * 72)

# ── Clean ─────────────────────────────────────────────────────────────────────

if ($Clean -and (Test-Path $OutputPath)) {
    Write-Host "Cleaning '$OutputPath'..."
    Remove-Item -Recurse -Force -Path $OutputPath
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null

# ── Helper: run dotnet and throw on failure ───────────────────────────────────

function Invoke-Dotnet {
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Arguments)
    Write-Host "  dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'dotnet $($Arguments[0])' failed with exit code $LASTEXITCODE."
    }
}

# ── Build and assemble each service package ───────────────────────────────────

foreach ($svc in $services) {
    $csproj = Get-ChildItem -Path $svc.ProjectDir -Filter '*.csproj' |
              Select-Object -First 1 -ExpandProperty FullName

    if (-not $csproj) {
        Write-Warning "No .csproj found in '$($svc.ProjectDir)'. Skipping $($svc.ProjectName)."
        continue
    }

    $pkgDir    = Join-Path $OutputPath $svc.PackageName
    $codeDir   = Join-Path $pkgDir 'Code'
    $configDir = Join-Path $pkgDir 'Config'

    Write-Host ''
    Write-Host "── $($svc.PackageName) ──"

    # 1. Publish
    New-Item -ItemType Directory -Force -Path $codeDir | Out-Null

    Invoke-Dotnet 'publish', $csproj,
        '--configuration',       $Configuration,
        '--runtime',             $RuntimeIdentifier,
        #'--self-contained',      'true',
        '--framework',           $TargetFramework,
        '--output',              $codeDir,
        '--nologo'

    # 2. Assemble Config/ by merging two sources:
    #    a) Source PackageRoot/Config  — provides SF-specific files (Settings.xml, etc.)
    #    b) dotnet publish output      — provides built/transformed appsettings*.json files
    #       When .csproj items link files from PackageRoot/Config/, dotnet preserves the
    #       relative path, so they land at Code\PackageRoot\Config\ in the publish output.
    #       The publish versions take precedence so any build-time transforms are preserved.
    $configSrc = Join-Path $svc.ProjectDir 'PackageRoot' 'Config'
    $hasSourceConfig = Test-Path $configSrc

    # Published appsettings land in Code\PackageRoot\Config\ (path preserved by dotnet publish)
    $publishedConfigDir = Join-Path $codeDir 'PackageRoot' 'Config'
    $publishedConfigs = Get-ChildItem -Path $publishedConfigDir -Filter 'appsettings*.json' -ErrorAction SilentlyContinue

    if ($hasSourceConfig -or $publishedConfigs) {
        New-Item -ItemType Directory -Force -Path $configDir | Out-Null

        if ($hasSourceConfig) {
            Copy-Item -Path (Join-Path $configSrc '*') -Destination $configDir -Recurse -Force
            Write-Host "  Copied source Config from $configSrc"
        }

        if ($publishedConfigs) {
            $publishedConfigs | Copy-Item -Destination $configDir -Force
            Write-Host "  Overlaid $($publishedConfigs.Count) appsettings file(s) from publish output"
        }
    }

    # 3. Copy ServiceManifest.xml
    $manifestSrc = Join-Path $svc.ProjectDir 'PackageRoot' 'ServiceManifest.xml'
    if (-not (Test-Path $manifestSrc)) {
        Write-Warning "ServiceManifest.xml not found at '$manifestSrc' for $($svc.ProjectName). Skipping manifest copy."
        continue
    }
    Copy-Item -Path $manifestSrc -Destination $pkgDir -Force
    Write-Host "  Copied ServiceManifest.xml"
}

# ── Copy ApplicationManifest.xml ──────────────────────────────────────────────

Write-Host ''
Copy-Item -Path $appManifestPath -Destination $OutputPath -Force
Write-Host "Copied ApplicationManifest.xml"

# ── Done ─────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host ('=' * 72)
Write-Host "  Package ready : $OutputPath"
Write-Host ('=' * 72)

# Return path so callers can pipe it directly into deployment scripts
$OutputPath