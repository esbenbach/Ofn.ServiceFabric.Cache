#Requires -Version 7.0
<#
.SYNOPSIS
    Shared utilities for the Service Fabric deployment scripts.

.DESCRIPTION
    Provides reusable functions used by Deploy-SFApp.ps1, Remove-SFApp.ps1, and
    Package-SFApp.ps1. Import this module at the top of each script:

        Import-Module (Join-Path $PSScriptRoot 'SFApp.Common.psm1') -Force

    Exported functions:
        Assert-SfctlAvailable           — throws if sfctl is not on PATH
        Invoke-Sfctl                    — sfctl wrapper with echo, capture, and failure handling
        Resolve-SfProjectRoot           — resolves the project root from SfProjPath / ProjectRoot / caller fallback
        Get-SfManifestInfo              — parses ApplicationManifest.xml (TypeName, TypeVersion)
        Get-SfAppParams                 — parses ApplicationParameters XML (AppName, AppId, ParamJson)
        Get-SfPublishProfile            — reads UpgradeDeployment flag from PublishProfiles XML
        Connect-SfCluster               — runs 'sfctl cluster select' for local or remote clusters
        Get-SfImageStoreConnectionString — reads ImageStoreConnectionString from the cluster manifest
        Test-SfAppExists                — returns $true when the application instance exists
        Test-SfTypeProvisioned          — returns $true when the application type version is registered
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── sfctl availability ────────────────────────────────────────────────────────

function Assert-SfctlAvailable {
    <#
    .SYNOPSIS
        Throws a descriptive error if sfctl is not found on PATH.
    #>
    if (-not (Get-Command sfctl -ErrorAction SilentlyContinue)) {
        throw @'
'sfctl' not found on PATH.  Install it with:
    pip install sfctl
Docs: https://learn.microsoft.com/azure/service-fabric/service-fabric-cli
'@
    }
}

# ── Core sfctl invoker ────────────────────────────────────────────────────────

function Invoke-Sfctl {
    <#
    .SYNOPSIS
        Invokes sfctl with the given arguments, echoing the command to the host.

    .PARAMETER Arguments
        Arguments forwarded verbatim to sfctl.

    .PARAMETER Capture
        When set, captures stdout and returns it as a string instead of streaming
        it to the terminal.

    .PARAMETER AllowFailure
        When set, a non-zero exit code is treated as a warning rather than an
        error. Used for "get if exists" or "delete if exists" operations.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)]
        [string[]]$Arguments,

        [switch]$Capture,
        [switch]$AllowFailure
    )

    Write-Host "  sfctl $($Arguments -join ' ')"

    if ($Capture) {
        $output = & sfctl @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            if ($AllowFailure) {
                Write-Warning "sfctl $($Arguments[0]) $($Arguments[1]) returned exit $LASTEXITCODE (ignored)."
                return ''
            }
            throw "sfctl $($Arguments[0]) $($Arguments[1]) failed (exit $LASTEXITCODE):`n$($output -join "`n")"
        }
        return ($output -join "`n")
    }

    & sfctl @Arguments
    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            Write-Warning "sfctl $($Arguments[0]) $($Arguments[1]) returned exit $LASTEXITCODE (ignored)."
            return
        }
        throw "sfctl $($Arguments[0]) $($Arguments[1]) failed (exit $LASTEXITCODE)."
    }
}

# ── Project structure helpers ─────────────────────────────────────────────────

function Resolve-SfProjectRoot {
    <#
    .SYNOPSIS
        Resolves the SF application project root from the provided hints.

    .DESCRIPTION
        Resolution priority:
          1. Parent folder of -SfProjPath (when provided).
          2. -ProjectRoot (when provided).
          3. Parent of -ScriptRoot (fallback for scripts that live inside the
             ServiceFabric/ sub-folder of the repo).

    .PARAMETER SfProjPath
        Path to the .sfproj file. Its parent folder becomes the project root.

    .PARAMETER ProjectRoot
        Explicit project root path.

    .PARAMETER ScriptRoot
        $PSScriptRoot of the calling script. Used as final fallback so that
        project root = parent of the scripts folder.
    #>
    param(
        [string]$SfProjPath,
        [string]$ProjectRoot,
        [string]$ScriptRoot
    )

    if ($SfProjPath) {
        if (-not (Test-Path $SfProjPath)) {
            throw "Service Fabric project file not found: '$SfProjPath'"
        }
        $root = Split-Path (Resolve-Path $SfProjPath).Path -Parent
    } elseif ($ProjectRoot) {
        $root = $ProjectRoot
    } else {
        $root = Split-Path $ScriptRoot -Parent
    }

    return (Resolve-Path $root).Path
}

function Get-SfManifestInfo {
    <#
    .SYNOPSIS
        Parses ApplicationManifest.xml and returns application type metadata.

    .PARAMETER ProjectRoot
        Root directory that contains ApplicationPackageRoot\ApplicationManifest.xml.

    .OUTPUTS
        Hashtable with keys: TypeName, TypeVersion, ManifestPath.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot
    )

    $manifestPath = Join-Path $ProjectRoot 'ApplicationPackageRoot' 'ApplicationManifest.xml'
    if (-not (Test-Path $manifestPath)) {
        throw "Required file not found: $manifestPath"
    }

    $xml         = [xml](Get-Content $manifestPath -Raw)
    $typeName    = $xml.ApplicationManifest.ApplicationTypeName
    $typeVersion = $xml.ApplicationManifest.ApplicationTypeVersion

    if (-not $typeName -or -not $typeVersion) {
        throw "Could not read ApplicationTypeName/ApplicationTypeVersion from '$manifestPath'."
    }

    return @{
        TypeName     = $typeName
        TypeVersion  = $typeVersion
        ManifestPath = $manifestPath
    }
}

function Get-SfAppParams {
    <#
    .SYNOPSIS
        Parses the environment's ApplicationParameters XML and returns app identity
        and an sfctl-ready parameter JSON string.

    .DESCRIPTION
        sfctl --parameters expects a JSON object {"Name":"Value",...}.
        Using a REST-API-style array causes "list indices must be integers or slices,
        not dict". This function always produces the correct format.

    .PARAMETER ProjectRoot
        Root directory that contains ApplicationParameters\$Environment.xml.

    .PARAMETER Environment
        Environment name used to locate $Environment.xml.

    .OUTPUTS
        Hashtable with keys: AppName (fabric:/...), AppId (without fabric:/ prefix),
        ParamJson (compact JSON object string).
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,

        [Parameter(Mandatory)]
        [string]$Environment
    )

    $paramsFile = Join-Path $ProjectRoot 'ApplicationParameters' "$Environment.xml"
    if (-not (Test-Path $paramsFile)) {
        throw "Required file not found: $paramsFile"
    }

    $xml     = [xml](Get-Content $paramsFile -Raw)
    $appName = $xml.Application.Name   # e.g. fabric:/MyApp.Dev

    if (-not $appName) {
        throw "Could not read Application Name from '$paramsFile'."
    }

    # sfctl uses the application ID without the 'fabric:/' prefix
    $appId = $appName -replace '^fabric:/', ''

    $paramHashtable = [ordered]@{}
    if ($xml.Application.Parameters.Parameter) {
        $xml.Application.Parameters.Parameter | ForEach-Object {
            $paramHashtable[$_.Name] = $_.Value
        }
    }

    return @{
        AppName   = $appName
        AppId     = $appId
        ParamJson = (ConvertTo-Json -InputObject $paramHashtable -Compress)
    }
}

function Get-SfPublishProfile {
    <#
    .SYNOPSIS
        Reads the environment's PublishProfile XML and returns whether an upgrade
        deployment is requested.

    .PARAMETER ProjectRoot
        Root directory that contains PublishProfiles\$Environment.xml.

    .PARAMETER Environment
        Environment name.

    .OUTPUTS
        [bool] — $true when the profile has UpgradeDeployment Enabled="true".
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,

        [Parameter(Mandatory)]
        [string]$Environment
    )

    $profileFile = Join-Path $ProjectRoot 'PublishProfiles' "$Environment.xml"
    if (-not (Test-Path $profileFile)) {
        throw "Required file not found: $profileFile"
    }

    $xml       = [xml](Get-Content $profileFile -Raw)
    $upgradeEl = $xml.PublishProfile.SelectSingleNode('UpgradeDeployment')
    return [bool]($upgradeEl -and $upgradeEl.GetAttribute('Enabled') -eq 'true')
}

# ── Cluster connectivity ──────────────────────────────────────────────────────

function Connect-SfCluster {
    <#
    .SYNOPSIS
        Runs 'sfctl cluster select' targeting either a local or remote cluster.

    .DESCRIPTION
        For local clusters: calls 'sfctl cluster select' with no arguments so
        sfctl auto-discovers the running local cluster. Passing --endpoint to a
        local cluster can confuse sfctl.

        For remote clusters: builds the full select command including --endpoint
        and any provided certificate parameters.

    .PARAMETER IsLocal
        When $true, selects the local cluster without --endpoint.

    .PARAMETER ClusterEndpoint
        HTTP management endpoint for remote clusters (e.g. https://mycluster:19080).

    .PARAMETER CertPemPath
        Path to the client certificate PEM file.

    .PARAMETER KeyPemPath
        Path to the private key PEM file when stored separately from the certificate.

    .PARAMETER CaCertPath
        Path to the CA certificate PEM file for server verification.

    .PARAMETER NoVerify
        Skip TLS server-certificate verification.
    #>
    param(
        [Parameter(Mandatory)]
        [bool]$IsLocal,

        [string]$ClusterEndpoint = 'http://localhost:19080',
        [string]$CertPemPath,
        [string]$KeyPemPath,
        [string]$CaCertPath,
        [switch]$NoVerify
    )

    if ($IsLocal) {
        Invoke-Sfctl 'cluster', 'select'
    } else {
        $selectArgs = [System.Collections.Generic.List[string]]::new()
        $selectArgs.AddRange([string[]]@('cluster', 'select', '--endpoint', $ClusterEndpoint))

        if ($CertPemPath) { $selectArgs.AddRange([string[]]@('--cert', $CertPemPath)) }
        if ($KeyPemPath)  { $selectArgs.AddRange([string[]]@('--key',  $KeyPemPath))  }
        if ($CaCertPath)  { $selectArgs.AddRange([string[]]@('--ca',   $CaCertPath))  }
        if ($NoVerify)    { $selectArgs.Add('--no-verify') }

        Invoke-Sfctl @selectArgs
    }
}

function Get-SfImageStoreConnectionString {
    <#
    .SYNOPSIS
        Reads the ImageStoreConnectionString from the connected cluster's manifest.

    .OUTPUTS
        [string] — e.g. 'fabric:ImageStore' or 'file:C:\SfDevCluster\Data\ImageStoreShare'.
        Falls back to 'fabric:ImageStore' when the value cannot be found.
    #>
    $json   = Invoke-Sfctl 'cluster', 'manifest' -Capture
    $xml    = [xml]($json | ConvertFrom-Json).manifest
    $result = $xml.ClusterManifest.FabricSettings.Section |
        Where-Object { $_.Name -eq 'Management' } |
        ForEach-Object { $_.Parameter | Where-Object { $_.Name -eq 'ImageStoreConnectionString' } } |
        Select-Object -ExpandProperty Value -First 1

    return $result ? $result : 'fabric:ImageStore'
}

# ── Cluster state queries ─────────────────────────────────────────────────────

function Test-SfAppExists {
    <#
    .SYNOPSIS
        Returns $true if an application instance with the given ID exists on the cluster.

    .PARAMETER AppId
        Application ID without the 'fabric:/' prefix (e.g. 'MyApp.Dev').
    #>
    param(
        [Parameter(Mandatory)]
        [string]$AppId
    )

    $raw  = Invoke-Sfctl 'application', 'info', '--application-id', $AppId -Capture -AllowFailure
    $info = try { $raw | ConvertFrom-Json } catch { $null }
    return ($null -ne $info -and $info.PSObject.Properties['name'])
}

function Test-SfTypeProvisioned {
    <#
    .SYNOPSIS
        Returns $true if the specified application type version is registered on the cluster.

    .DESCRIPTION
        Uses sfctl's JSON response where the property is 'version' (not REST API's
        'applicationTypeVersion').

    .PARAMETER TypeName
        Application type name.

    .PARAMETER TypeVersion
        Application type version string.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$TypeName,

        [Parameter(Mandatory)]
        [string]$TypeVersion
    )

    $json  = Invoke-Sfctl 'application', 'type', '--application-type-name', $TypeName -Capture -AllowFailure
    $items = try { ($json | ConvertFrom-Json).items } catch { @() }
    return @($items | Where-Object { $_.version -eq $TypeVersion }).Count -gt 0
}

Export-ModuleMember -Function *
