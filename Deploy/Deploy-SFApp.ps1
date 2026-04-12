#Requires -Version 7.0
<#
.SYNOPSIS
    Deploys or upgrades any Service Fabric application using sfctl.

.DESCRIPTION
    Generic, application-agnostic deployment script. Uses sfctl (the cross-platform
    Service Fabric CLI) to perform steps 1-4 of a typical SF deployment:

      1. Select the target cluster.
      2. Upload the application package to the image store.
      3. Provision the application type (skipped when already provisioned).
      4. Create or upgrade the application instance.

    Service creation is intentionally omitted — define services in your
    ApplicationManifest.xml DefaultServices section, or create them separately.

    The application name, type, version, and parameters are derived automatically
    from ApplicationManifest.xml and the environment's ApplicationParameters XML,
    so no values are hardcoded in the script.

    NOTE: sfctl uses the HTTP management gateway (default port 19080), not the
    TCP client port (19000) used by the .NET Service Fabric SDK.

    Local environments (Local.1Node, Local.5Node) are handled specially:
      - 'sfctl cluster select' is called without --endpoint (uses the running
        local cluster automatically).
      - The upload always includes --compress and --imagestore-string, read from
        the cluster manifest (local clusters use a file-backed image store).
      - Certificate parameters are not forwarded.

    Supported environments:
        Dev         -> ASPNETCORE_ENVIRONMENT=Dev
        QA          -> ASPNETCORE_ENVIRONMENT=QA
        Prod        -> ASPNETCORE_ENVIRONMENT=Prod
        Local.1Node -> ASPNETCORE_ENVIRONMENT=Development
        Local.5Node -> ASPNETCORE_ENVIRONMENT=Development

.PARAMETER Environment
    Target environment. Must be one of: Dev, QA, Prod, Local.1Node, Local.5Node.

.PARAMETER SfProjPath
    Path to the .sfproj file. When provided the project's parent folder is used
    as the root directory for locating ApplicationManifest.xml, ApplicationParameters,
    and PublishProfiles. Takes precedence over -ProjectRoot.

.PARAMETER ProjectRoot
    Root directory of the Service Fabric application project (the folder that contains
    ApplicationPackageRoot\ and ApplicationParameters\). Ignored when -SfProjPath is
    supplied. Falls back to the parent of the script's own directory when neither
    parameter is specified.

.PARAMETER PackagePath
    Path to the pre-built package folder produced by Package-SFApp.ps1.
    Defaults to pkg/<ApplicationTypeName> under the project root, matching
    the default output folder of Package-SFApp.ps1.

.PARAMETER ClusterEndpoint
    HTTP management endpoint of the cluster, e.g. 'https://mycluster:19080'.
    Default: 'http://localhost:19080'. Ignored for Local environments.

.PARAMETER CertPemPath
    Path to a PEM file containing the client certificate (and optionally the
    private key) for authenticating to a secured cluster. Not used for Local.

.PARAMETER KeyPemPath
    Path to a PEM file containing only the private key when it is stored
    separately from -CertPemPath. Not used for Local.

.PARAMETER CaCertPath
    Path to the CA certificate PEM file used to verify the cluster's server
    certificate. Omit to use the system CA bundle. Not used for Local.

.PARAMETER NoVerify
    Skip TLS server certificate verification. Use only in dev/test environments.
    Not used for Local.

.PARAMETER DeployOnly
    Upload and provision the application type but do not create or upgrade the
    application instance. Useful for pre-staging packages.

.PARAMETER ForceUpgrade
    Force an UnmonitoredAuto upgrade regardless of the PublishProfile setting.

.PARAMETER ImageStorePath
    Relative path used inside the image store for upload and provision.
    sfctl application upload always registers the package in the image store
    under the leaf folder name of -PackagePath, so this value must match that
    name exactly or provision will fail with FABRIC_E_DIRECTORY_NOT_FOUND.
    Defaults to the leaf folder name of -PackagePath (e.g. 'MyAppType').

.PARAMETER UploadTimeoutSec
    Timeout in seconds for the package upload step. Default: 300.

.EXAMPLE
    # Deploy to the local single-node cluster
    .\Deploy-SFApp.ps1 -Environment Local.1Node `
        -SfProjPath .\MyApp.sfproj

.EXAMPLE
    # Upgrade Dev on a remote cluster
    .\Deploy-SFApp.ps1 -Environment Dev `
        -SfProjPath     .\MyApp.sfproj `
        -ClusterEndpoint 'https://dev-sf.example.com:19080' `
        -CertPemPath     '~/.sf/client.pem' `
        -CaCertPath      '~/.sf/ca.pem'

.EXAMPLE
    # Package then deploy to QA
    $pkg = .\Package-SFApp.ps1 -SfProjPath .\MyApp.sfproj -Configuration Release
    .\Deploy-SFApp.ps1 -Environment QA `
        -SfProjPath      .\MyApp.sfproj `
        -PackagePath     $pkg `
        -ClusterEndpoint 'https://qa-sf.example.com:19080' `
        -CertPemPath     '~/.sf/client.pem' `
        -CaCertPath      '~/.sf/ca.pem'

.EXAMPLE
    # Provision type only (no application instance created/upgraded)
    .\Deploy-SFApp.ps1 -Environment Local.1Node -SfProjPath .\MyApp.sfproj -DeployOnly

.EXAMPLE
    # Force an unmonitored-auto upgrade on Dev
    .\Deploy-SFApp.ps1 -Environment Dev -SfProjPath .\MyApp.sfproj -ForceUpgrade
#>

[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)]
    [ValidateSet('Dev', 'QA', 'Prod', 'Local.1Node', 'Local.5Node')]
    [string]$Environment,

    [Parameter()]
    [string]$SfProjPath,

    [Parameter()]
    [string]$ProjectRoot,

    [Parameter()]
    [string]$PackagePath,

    [Parameter()]
    [string]$ClusterEndpoint = 'http://localhost:19080',

    # PEM-based cert auth (cross-platform; what sfctl supports natively).
    # Not used for Local environments.
    [Parameter()]
    [string]$CertPemPath,

    [Parameter()]
    [string]$KeyPemPath,

    [Parameter()]
    [string]$CaCertPath,

    [Parameter()]
    [switch]$NoVerify,

    [Parameter()]
    [switch]$DeployOnly,

    [Parameter()]
    [switch]$ForceUpgrade,

    [Parameter()]
    [string]$ImageStorePath,

    [Parameter()]
    [int]$UploadTimeoutSec = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'SFApp.Common.psm1') -Force

# ── Setup ─────────────────────────────────────────────────────────────────────

Assert-SfctlAvailable
$isLocal      = $Environment -like 'Local.*'
$resolvedRoot = Resolve-SfProjectRoot -SfProjPath $SfProjPath -ProjectRoot $ProjectRoot -ScriptRoot $PSScriptRoot

# ── Parse project manifests ───────────────────────────────────────────────────

$manifest       = Get-SfManifestInfo   -ProjectRoot $resolvedRoot
$appTypeName    = $manifest.TypeName
$appTypeVersion = $manifest.TypeVersion

$appParams = Get-SfAppParams -ProjectRoot $resolvedRoot -Environment $Environment
$appName   = $appParams.AppName
$appId     = $appParams.AppId
$paramJson = $appParams.ParamJson

$isUpgrade = $ForceUpgrade.IsPresent -or (Get-SfPublishProfile -ProjectRoot $resolvedRoot -Environment $Environment)

# ── Resolve PackagePath ───────────────────────────────────────────────────────
# Default to pkg/<TypeName> — consistent with Package-SFApp.ps1's default output.
# The ImageStorePath must match the leaf folder name sfctl upload uses as the
# image store key, or provision will fail with FABRIC_E_DIRECTORY_NOT_FOUND.

if (-not $PackagePath) {
    $PackagePath = Join-Path $resolvedRoot 'pkg' $appTypeName
}

if (-not (Test-Path $PackagePath)) {
    throw "Package path not found: $PackagePath`nRun Package-SFApp.ps1 first, or specify -PackagePath."
}

$PackagePath = (Resolve-Path $PackagePath).Path

if (-not $ImageStorePath) {
    $ImageStorePath = Split-Path $PackagePath -Leaf
}

# ── Print summary ─────────────────────────────────────────────────────────────

Write-Host ('=' * 72)
Write-Host '  Service Fabric Application Deployment  (sfctl)'
Write-Host ('=' * 72)
Write-Host "  Environment     : $Environment"
Write-Host "  App name        : $appName"
Write-Host "  App type        : $appTypeName  v$appTypeVersion"
Write-Host "  Package path    : $PackagePath"
Write-Host "  Image store key : $ImageStorePath"
Write-Host "  Cluster         : $(if ($isLocal) { 'localhost (local cluster)' } else { $ClusterEndpoint })"
Write-Host "  Mode            : $(if ($isUpgrade) { 'Upgrade (UnmonitoredAuto)' } else { 'Create' })"
Write-Host ('-' * 72)

# ── Step 1: Select cluster ────────────────────────────────────────────────────

Write-Host ''
Write-Host '[1/4] Selecting cluster...'
Connect-SfCluster -IsLocal $isLocal -ClusterEndpoint $ClusterEndpoint `
    -CertPemPath $CertPemPath -KeyPemPath $KeyPemPath -CaCertPath $CaCertPath -NoVerify:$NoVerify

$imageStoreConnStr = Get-SfImageStoreConnectionString
Write-Host "  ImageStoreConnectionString: $imageStoreConnStr"

# ── Steps 2-3: Upload & Provision ────────────────────────────────────────────

Write-Host ''
Write-Host '[2/4] Checking provisioned application types...'

if (Test-SfTypeProvisioned -TypeName $appTypeName -TypeVersion $appTypeVersion) {
    Write-Host "  '$appTypeName' v$appTypeVersion already provisioned — skipping upload."
} else {
    # ── Upload package ─────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '[2/4] Uploading package to image store...'

    $uploadArgs = [System.Collections.Generic.List[string]]::new()
    $uploadArgs.AddRange([string[]]@(
        'application', 'upload',
        '--path',          $PackagePath,
        '--show-progress',
        '--timeout',       $UploadTimeoutSec.ToString(),
        '--compress'
    ))

    # Local clusters and file-backed remote clusters need an explicit --imagestore-string
    # because sfctl cannot auto-detect file-backed stores.
    if ($isLocal -or $imageStoreConnStr -like 'file:*') {
        $uploadArgs.AddRange([string[]]@('--imagestore-string', $imageStoreConnStr))
    }

    Invoke-Sfctl @uploadArgs

    # ── Provision application type ─────────────────────────────────────────────
    Write-Host ''
    Write-Host '[3/4] Provisioning application type...'
    Invoke-Sfctl 'application', 'provision', '--application-type-build-path', $ImageStorePath
}

# ── Step 4: Create or upgrade application instance ───────────────────────────

if ($DeployOnly) {
    Write-Host ''
    Write-Host '[4/4] -DeployOnly specified — skipping application instance create/upgrade.'
} else {
    Write-Host ''
    Write-Host '[4/4] Creating or upgrading application instance...'

    if (Test-SfAppExists -AppId $appId) {
        $upgradeMsg = if ($isUpgrade) { "Upgrading '$appName' to v$appTypeVersion (UnmonitoredAuto)..." }
                      else            { "'$appName' already exists; upgrading to v$appTypeVersion..." }
        Write-Host "  $upgradeMsg"

        $upgradeArgs = [System.Collections.Generic.List[string]]::new()
        $upgradeArgs.AddRange([string[]]@(
            'application', 'upgrade',
            '--application-id',      $appId,
            '--application-version', $appTypeVersion,
            '--parameters',          $paramJson,
            '--mode',                'UnmonitoredAuto'
        ))
        if ($ForceUpgrade) {
            # Skip stuck replicas and force restart — equivalent to VS 'Force' upgrade
            $upgradeArgs.AddRange([string[]]@(
                '--force-restart',             'true',
                '--replica-set-check-timeout', '1'
            ))
        }
        Invoke-Sfctl @upgradeArgs

    } else {
        Write-Host "  Creating new application instance '$appName'..."
        Invoke-Sfctl 'application', 'create',
            '--app-name',    $appName,
            '--app-type',    $appTypeName,
            '--app-version', $appTypeVersion,
            '--parameters',  $paramJson
    }
}

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host ('=' * 72)
Write-Host "  Deployment to '$Environment' ($appName) completed successfully."
Write-Host ('=' * 72)
