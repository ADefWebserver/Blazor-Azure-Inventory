#Requires -Version 7.3
<#
.SYNOPSIS
    Builds the web and/or MCP images, pushes them to Azure Container Registry, and updates the existing container apps.

.DESCRIPTION
    All deployment values come from a git-ignored appsettings.Org.json (see
    BlazorAzureInventorySolution.AppHost/appsettings.Org.sample.json). The script only changes the image and, for the
    web app, the Security__AllowedTenantIds__*, UserManagement__BootstrapAdmins, and UserManagement__SupportContact
    environment variables. Identities, volumes, ingress, and scale settings are left as they are.

.EXAMPLE
    ./deploy/Publish-AzInventory.ps1 -Component Web -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SettingsPath = (Join-Path $PSScriptRoot '..' 'BlazorAzureInventorySolution.AppHost' 'appsettings.Org.json'),

    [ValidateSet('Web', 'Mcp', 'All')]
    [string]$Component = 'Web',

    [string]$Tag = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'),

    # Base image tag for AzureMcpServer/Dockerfile; empty uses the Dockerfile default.
    [string]$AzureMcpTag = '',

    [switch]$SkipEnvSync
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repoRoot = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path $SettingsPath)) {
    throw "Settings file not found: $SettingsPath. Copy appsettings.Org.json from your organization's private bundle, or create it from appsettings.Org.sample.json."
}
$settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
$deployment = $settings.Deployment

function Get-Required([string]$Name, [object]$Value) {
    if ([string]::IsNullOrWhiteSpace([string]$Value) -or [string]$Value -match '^<.*>$') {
        throw "Deployment setting '$Name' is missing or still a placeholder in $SettingsPath."
    }
    [string]$Value
}

$subscription = Get-Required 'Deployment:SubscriptionId' $deployment.SubscriptionId
$resourceGroup = Get-Required 'Deployment:ResourceGroup' $deployment.ResourceGroup
$registry = Get-Required 'Deployment:Registry:Name' $deployment.Registry.Name
$registrySubscription = Get-Required 'Deployment:Registry:SubscriptionId' $deployment.Registry.SubscriptionId

$targets = @()
if ($Component -in 'Web', 'All') {
    $targets += [pscustomobject]@{
        Kind       = 'Web'
        App        = Get-Required 'Deployment:WebAppName' $deployment.WebAppName
        Repository = Get-Required 'Deployment:WebImageRepository' $deployment.WebImageRepository
    }
}
if ($Component -in 'Mcp', 'All') {
    $targets += [pscustomobject]@{
        Kind       = 'Mcp'
        App        = Get-Required 'Deployment:McpAppName' $deployment.McpAppName
        Repository = Get-Required 'Deployment:McpImageRepository' $deployment.McpImageRepository
    }
}

foreach ($tool in 'az', 'podman', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' is not on the PATH." }
}
az account show -o none
$loginServer = az acr show --name $registry --subscription $registrySubscription --query loginServer -o tsv

function Get-WebEnvPlan([string]$App) {
    $tenantIds = @($settings.Security.AllowedTenantIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $bootstrapAdmins = [string]$settings.UserManagement.BootstrapAdmins
    $supportContact = [string]$settings.UserManagement.SupportContact
    if ([string]::IsNullOrWhiteSpace($bootstrapAdmins)) {
        throw "UserManagement:BootstrapAdmins is empty in $SettingsPath. Refusing to sync environment variables (use -SkipEnvSync for an image-only update)."
    }

    $planned = [ordered]@{}
    for ($i = 0; $i -lt $tenantIds.Count; $i++) { $planned["Security__AllowedTenantIds__$i"] = $tenantIds[$i] }
    $planned['UserManagement__BootstrapAdmins'] = $bootstrapAdmins
    if (-not [string]::IsNullOrWhiteSpace($supportContact)) { $planned['UserManagement__SupportContact'] = $supportContact }

    $current = @{}
    $containerApp = az containerapp show --name $App --resource-group $resourceGroup --subscription $subscription -o json | ConvertFrom-Json
    foreach ($env in $containerApp.properties.template.containers[0].env) { $current[$env.name] = $env.value }

    $remove = @()
    if ($tenantIds.Count -eq 0) {
        Write-Warning 'Security:AllowedTenantIds is empty; existing tenant variables on the app are left unchanged.'
    }
    else {
        $remove = @($current.Keys | Where-Object { $_ -match '^Security__AllowedTenantIds__(\d+)$' -and [int]$Matches[1] -ge $tenantIds.Count })
    }

    $rows = foreach ($name in $planned.Keys) {
        $change = if ($current[$name] -ceq $planned[$name]) { 'unchanged' } elseif ($current.ContainsKey($name)) { 'update' } else { 'add' }
        "  [$change] $name = $($planned[$name])"
    }
    $rows += foreach ($name in $remove) { "  [remove] $name (was $($current[$name]))" }
    Write-Host 'Environment variables:'
    $rows | ForEach-Object { Write-Host $_ }

    [pscustomobject]@{
        Set    = @($planned.Keys | ForEach-Object { "$_=$($planned[$_])" })
        Remove = $remove
    }
}

$loggedIn = $false
foreach ($target in $targets) {
    $localImage = "$($target.Repository):$Tag"
    $remoteImage = "$loginServer/$($target.Repository):$Tag"
    Write-Host "== $($target.Kind): $($target.App) -> $remoteImage" -ForegroundColor Cyan

    $envPlan = if ($target.Kind -eq 'Web' -and -not $SkipEnvSync) { Get-WebEnvPlan $target.App }

    if ($PSCmdlet.ShouldProcess($localImage, 'Build image')) {
        if ($target.Kind -eq 'Web') {
            dotnet publish (Join-Path $repoRoot 'BlazorAzureInventory' 'BlazorAzureInventory.csproj') -c Release --os linux --arch x64 `
                -t:PublishContainer -p:LocalRegistry=Podman "-p:ContainerRepository=$($target.Repository)" "-p:ContainerImageTag=$Tag"
        }
        else {
            $buildArgs = @('build', '-t', $localImage)
            if ($AzureMcpTag) { $buildArgs += @('--build-arg', "AZURE_MCP_TAG=$AzureMcpTag") }
            podman @buildArgs (Join-Path $repoRoot 'AzureMcpServer')
        }
    }

    if ($PSCmdlet.ShouldProcess($remoteImage, 'Push image')) {
        if (-not $loggedIn) {
            # Publishing straight to ACR fails with CONTAINER1016, so push through Podman with an ACR access token.
            az acr login --name $registry --subscription $registrySubscription --expose-token --query accessToken -o tsv |
                podman login $loginServer --username 00000000-0000-0000-0000-000000000000 --password-stdin
            $loggedIn = $true
        }
        podman tag $localImage $remoteImage
        podman push $remoteImage
    }

    $updateArgs = @('containerapp', 'update', '--name', $target.App, '--resource-group', $resourceGroup, '--subscription', $subscription, '--image', $remoteImage)
    if ($envPlan -and $envPlan.Set.Count) { $updateArgs += '--set-env-vars'; $updateArgs += $envPlan.Set }
    if ($envPlan -and $envPlan.Remove.Count) { $updateArgs += '--remove-env-vars'; $updateArgs += $envPlan.Remove }

    if ($PSCmdlet.ShouldProcess($target.App, 'Update container app image and environment variables')) {
        az @updateArgs -o none

        $revision = az containerapp revision list --name $target.App --resource-group $resourceGroup --subscription $subscription -o json |
            ConvertFrom-Json | Sort-Object { $_.properties.createdTime } -Descending | Select-Object -First 1
        Write-Host ("Revision {0}: health {1}, running state {2}" -f $revision.name, $revision.properties.healthState, $revision.properties.runningState)
    }
}
