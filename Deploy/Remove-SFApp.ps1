#Requires -Version 7.0
<#
.SYNOPSIS
    Removes a deployed SF App from a Service Fabric cluster.

.DESCRIPTION
    Uses sfctl to cleanly uninstall an SF App in three steps:

      1. Delete the application instance (which implicitly deletes all its services).
      2. Unregister the application type version (unless -KeepType is specified).
      3. Delete the package from the image store (unless -KeepImageStore is specified).

    Application name, type name, and version are derived automatically from
    ApplicationManifest.xml and the environment's ApplicationParameters XML 

    A confirmation prompt is shown before any destructive action unless -Force is specified.

    NOTE: sfctl uses the HTTP management gateway (default port 19080).

    Supported environments:
        Dev
        QA
        Prod 
        Local.1Node
        Local.5Node

.PARAMETER Environment
    Target environment to remove. Must be one of: Dev, QA, Prod, Local.1Node, Local.5Node.

.PARAMETER ClusterEndpoint
    HTTP management endpoint of the cluster. Default: 'http://localhost:19080'.

.PARAMETER CertPemPath
    Path to a PEM file containing the client certificate for a secured cluster.

.PARAMETER KeyPemPath
    Path to a PEM file containing only the private key (when separate from -CertPemPath).

.PARAMETER CaCertPath
    Path to the CA certificate PEM file for server verification.

.PARAMETER NoVerify
    Skip TLS server certificate verification. Use only in dev/test environments.

.PARAMETER ImageStorePath
    Image store relative path that was used during deployment. Defaults to
    '<ApplicationTypeName>' (e.g. MyAppType), which matches the default used
    by Deploy-SFApp.ps1 (the leaf folder name of the uploaded package).

.PARAMETER KeepType
    Skip unregistering the application type. Useful when you intend to redeploy
    immediately and want to avoid re-uploading the package.

.PARAMETER KeepImageStore
    Skip removing the package from the image store.

.PARAMETER SfProjPath
    Path to the .sfproj file. When provided the project's parent folder is used
    as the root directory for locating ApplicationManifest.xml and ApplicationParameters.
    Takes precedence over -ProjectRoot.

.PARAMETER ProjectRoot
    Root directory of the Service Fabric application project (the folder that contains
    ApplicationPackageRoot\ and ApplicationParameters\). Ignored when -SfProjPath is
    supplied. Falls back to the parent of the script's own directory when neither
    parameter is specified.

.PARAMETER Force
    Suppress the confirmation prompt before performing destructive operations.

.EXAMPLE
    # Remove from the local cluster (prompts for confirmation)
    .\Remove-SFApp.ps1 -Environment Local.1Node -SfProjPath .\MyApp.sfproj

.EXAMPLE
    # Remove from local without prompting
    .\Remove-SFApp.ps1 -Environment Local.1Node -SfProjPath .\MyApp.sfproj -Force

.EXAMPLE
    # Remove from a secured remote cluster
    .\Remove-SFApp.ps1 -Environment Dev `
        -SfProjPath      .\MyApp.sfproj `
        -ClusterEndpoint 'https://dev-sf.example.com:19080' `
        -CertPemPath     '~/.sf/client.pem' `
        -CaCertPath      '~/.sf/ca.pem' `
        -Force

.EXAMPLE
    # Remove the app instance only; keep type and image store (fast redeploy)
    .\Remove-SFApp.ps1 -Environment Local.1Node -SfProjPath .\MyApp.sfproj -KeepType -KeepImageStore -Force
#>

[CmdletBinding(SupportsShouldProcess)]
param (
    [Parameter(Mandatory)]
    [ValidateSet('Dev', 'QA', 'Prod', 'Local.1Node', 'Local.5Node')]
    [string]$Environment,

    [Parameter()]
    [string]$SfProjPath,

    [Parameter()]
    [string]$ClusterEndpoint = 'http://localhost:19080',

    [Parameter()]
    [string]$CertPemPath,

    [Parameter()]
    [string]$KeyPemPath,

    [Parameter()]
    [string]$CaCertPath,

    [Parameter()]
    [switch]$NoVerify,

    [Parameter()]
    [string]$ImageStorePath,

    [Parameter()]
    [switch]$KeepType,

    [Parameter()]
    [switch]$KeepImageStore,

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [string]$ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'SFApp.Common.psm1') -Force

# ── Setup ─────────────────────────────────────────────────────────────────────

Assert-SfctlAvailable
$isLocal      = $Environment -like 'Local.*'
$resolvedRoot = Resolve-SfProjectRoot -SfProjPath $SfProjPath -ProjectRoot $ProjectRoot -ScriptRoot $PSScriptRoot

# ── Parse project manifests ───────────────────────────────────────────────────

$manifest       = Get-SfManifestInfo -ProjectRoot $resolvedRoot
$appTypeName    = $manifest.TypeName
$appTypeVersion = $manifest.TypeVersion

$appParams = Get-SfAppParams -ProjectRoot $resolvedRoot -Environment $Environment
$appName   = $appParams.AppName
$appId     = $appParams.AppId

if (-not $ImageStorePath) {
    $ImageStorePath = $appTypeName
}

# ── Print summary ─────────────────────────────────────────────────────────────

Write-Host ('=' * 72)
Write-Host '  SF Application Removal  (sfctl)'
Write-Host ('=' * 72)
Write-Host "  Environment  : $Environment"
Write-Host "  App name     : $appName"
Write-Host "  App type     : $appTypeName  v$appTypeVersion"
Write-Host "  Image store  : $ImageStorePath"
Write-Host "  Cluster      : $ClusterEndpoint"
Write-Host ''
Write-Host "  Actions to perform:"
Write-Host "    [1] Delete application instance '$appName'"
if (-not $KeepType)       { Write-Host "    [2] Unregister type '$appTypeName' v$appTypeVersion" }
if (-not $KeepImageStore) { Write-Host "    [3] Remove '$ImageStorePath' from image store" }
Write-Host ('-' * 72)

# ── Confirmation ──────────────────────────────────────────────────────────────

if (-not $Force) {
    $answer = Read-Host "Proceed? This will DELETE '$appName' from '$ClusterEndpoint'. [y/N]"
    if ($answer -notmatch '^[Yy]') {
        Write-Host 'Aborted.'
        exit 0
    }
}

# ── Step 1: Select cluster ────────────────────────────────────────────────────

Write-Host ''
Write-Host '[1/3] Selecting cluster...'
Connect-SfCluster -IsLocal $isLocal -ClusterEndpoint $ClusterEndpoint `
    -CertPemPath $CertPemPath -KeyPemPath $KeyPemPath -CaCertPath $CaCertPath -NoVerify:$NoVerify

# ── Step 2: Delete application instance ──────────────────────────────────────

Write-Host ''
Write-Host '[2/3] Deleting application instance...'

if (Test-SfAppExists -AppId $appId) {
    Write-Host "  Deleting '$appName'..."
    Invoke-Sfctl 'application', 'delete', '--application-id', $appId
    Write-Host "  Deleted."
} else {
    Write-Host "  Application '$appName' not found — skipping."
}

# ── Step 3: Unregister application type ──────────────────────────────────────

if ($KeepType) {
    Write-Host ''
    Write-Host '[3/3] Skipping type unregistration (-KeepType).'
} else {
    Write-Host ''
    Write-Host "[3/3] Unregistering application type '$appTypeName' v$appTypeVersion..."

    if (Test-SfTypeProvisioned -TypeName $appTypeName -TypeVersion $appTypeVersion) {
        Invoke-Sfctl 'application', 'unprovision',
            '--application-type-name',    $appTypeName,
            '--application-type-version', $appTypeVersion
        Write-Host "  Type unregistered."
    } else {
        Write-Host "  Type '$appTypeName' v$appTypeVersion not registered — skipping."
    }
}

# ── Step 4: Remove from image store ──────────────────────────────────────────

if ($KeepImageStore) {
    Write-Host ''
    Write-Host 'Skipping image store cleanup (-KeepImageStore).'
} else {
    Write-Host ''
    Write-Host "Removing '$ImageStorePath' from image store..."
    Invoke-Sfctl 'store', 'delete', '--content-path', $ImageStorePath -AllowFailure
    Write-Host "  Image store entry removed (or was already absent)."
}

# ── Done ──────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host ('=' * 72)
Write-Host "  '$Environment' ($appName) removed successfully."
Write-Host ('=' * 72)
