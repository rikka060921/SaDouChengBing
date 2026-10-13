[CmdletBinding()]
param(
    [string]$SourcePath = 'C:\Deploy\ToDo-source',
    [string]$Branch = 'Work-miracles(SaDouChengBing)',
    [string]$SiteName = 'ToDo',
    [string]$AppPoolName = 'ToDo',
    [string]$SiteRoot = 'C:\inetpub\ToDo',
    [ValidateRange(1, 1000)]
    [int]$KeepReleases = 3,
    [string]$HealthCheckUrl = 'http://127.0.0.1/health/ready',
    [string]$HealthCheckHost = '',
    [int]$HealthCheckAttempts = 12
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ToDoRuntimeData.ps1')

function Invoke-Native {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Wait-WebAppPoolState {
    param(
        [string]$Name,
        [ValidateSet('Started', 'Stopped')]
        [string]$ExpectedState,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $state = (Get-WebAppPoolState -Name $Name).Value
        if ($state -eq $ExpectedState) {
            return
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    throw "IIS application pool '$Name' did not reach state '$ExpectedState' within $TimeoutSeconds seconds."
}

function Wait-ApplicationReady {
    param(
        [string]$Url,
        [string]$HostHeader,
        [int]$Attempts
    )

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return
    }

    $headers = @{}
    if (-not [string]::IsNullOrWhiteSpace($HostHeader)) {
        $headers['Host'] = $HostHeader
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Headers $headers -UseBasicParsing -TimeoutSec 10
            if ($response.StatusCode -eq 200) {
                return
            }
            $lastError = "HTTP $($response.StatusCode)"
        }
        catch {
            $lastError = $_.Exception.Message
        }

        if ($attempt -lt $Attempts) {
            Start-Sleep -Seconds 5
        }
    }

    throw "Application readiness check failed after $Attempts attempts: $lastError"
}

if (-not (Test-Path (Join-Path $SourcePath '.git'))) {
    throw "Source repository was not found at $SourcePath. Clone origin there first."
}

Import-Module WebAdministration
$releasesPath = Join-Path $SiteRoot 'releases'
New-Item -ItemType Directory -Force -Path $releasesPath | Out-Null

$site = Get-Website -Name $SiteName -ErrorAction SilentlyContinue
if ($null -eq $site) {
    throw "IIS site '$SiteName' does not exist. Create the site and its app pool once before deploying."
}
$currentPath = [string]$site.physicalPath
if ([string]::IsNullOrWhiteSpace($HealthCheckHost)) {
    $httpBinding = Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $httpBinding) {
        $bindingParts = ([string]$httpBinding.bindingInformation).Split(':')
        if ($bindingParts.Count -ge 3 -and -not [string]::IsNullOrWhiteSpace($bindingParts[2])) {
            $HealthCheckHost = $bindingParts[2]
        }
    }
}

Push-Location $SourcePath
try {
    Invoke-Native 'git' @('fetch', 'origin', $Branch)
    $status = (& git status --porcelain)
    if ($status) {
        throw "The deployment source has local changes. Commit or discard them before deploying."
    }
    Invoke-Native 'git' @('checkout', $Branch)
    Invoke-Native 'git' @('pull', '--ff-only', 'origin', $Branch)

    $releaseName = Get-Date -Format 'yyyyMMdd-HHmmss'
    $releasePath = Join-Path $releasesPath $releaseName
    Invoke-Native 'dotnet' @('publish', '.\ToDo.Razor\ToDo.Razor.csproj', '-c', 'Release', '-o', $releasePath)
}
finally {
    Pop-Location
}

$previousPath = $currentPath
$physicalPathChanged = $false
try {
    $poolState = (Get-WebAppPoolState -Name $AppPoolName).Value
    if ($poolState -ne 'Stopped') {
        if ($poolState -ne 'Stopping') {
            Stop-WebAppPool -Name $AppPoolName
        }
        Wait-WebAppPoolState -Name $AppPoolName -ExpectedState 'Stopped'
    }

    # Copy runtime contents after stopping writers. Copying the directory itself
    # into an existing publish directory creates App_Data/App_Data or uploads/uploads.
    if (Test-Path -LiteralPath $currentPath) {
        $oldWebConfig = Join-Path $currentPath 'web.config'
        if (Test-Path -LiteralPath $oldWebConfig) {
            Copy-Item -LiteralPath $oldWebConfig -Destination (Join-Path $releasePath 'web.config') -Force
        }
        foreach ($relativePath in @('App_Data', 'wwwroot\uploads')) {
            $oldPath = Join-Path $currentPath $relativePath
            if (Test-Path -LiteralPath $oldPath -PathType Container) {
                Copy-ToDoRuntimeDirectory -Source $oldPath -Destination (Join-Path $releasePath $relativePath) | Out-Null
            }
        }
        # Recover only absent files from any legacy nested copy in the NEW release.
        Repair-ToDoRuntimeNesting -ReleasePath $releasePath | Out-Null
    }

    $appPoolIdentity = "IIS AppPool\$AppPoolName"
    foreach ($relativePath in @('App_Data', 'wwwroot\uploads')) {
        $writablePath = Join-Path $releasePath $relativePath
        New-Item -ItemType Directory -Force -Path $writablePath | Out-Null
        Invoke-Native 'icacls.exe' @($writablePath, '/grant', "${appPoolIdentity}:(OI)(CI)M", '/T', '/C')
    }

    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $releasePath
    $physicalPathChanged = $true
    Start-WebAppPool -Name $AppPoolName
    Wait-WebAppPoolState -Name $AppPoolName -ExpectedState 'Started'
    Wait-ApplicationReady -Url $HealthCheckUrl -HostHeader $HealthCheckHost -Attempts $HealthCheckAttempts
}
catch {
    $deploymentError = $_
    try {
        $poolState = (Get-WebAppPoolState -Name $AppPoolName).Value
        if ($poolState -eq 'Stopping') {
            Wait-WebAppPoolState -Name $AppPoolName -ExpectedState 'Stopped'
            $poolState = 'Stopped'
        }

        if ($physicalPathChanged) {
            Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $previousPath
        }

        if ($poolState -eq 'Starting') {
            Wait-WebAppPoolState -Name $AppPoolName -ExpectedState 'Started'
        }
        elseif ($poolState -ne 'Started') {
            Start-WebAppPool -Name $AppPoolName
            Wait-WebAppPoolState -Name $AppPoolName -ExpectedState 'Started'
        }
    }
    catch {
        Write-Warning "Automatic IIS rollback failed: $($_.Exception.Message)"
    }
    throw $deploymentError
}

$releaseRoot = [System.IO.Path]::GetFullPath($releasesPath).TrimEnd([char[]]@('\', '/'))
$activeRelease = [System.IO.Path]::GetFullPath(
    [Environment]::ExpandEnvironmentVariables([string](Get-Website -Name $SiteName).PhysicalPath)
).TrimEnd([char[]]@('\', '/'))
foreach ($oldRelease in @(Get-ChildItem -LiteralPath $releasesPath -Directory |
    Sort-Object Name -Descending | Select-Object -Skip $KeepReleases)) {
    $oldPath = [System.IO.Path]::GetFullPath($oldRelease.FullName).TrimEnd([char[]]@('\', '/'))
    $oldParent = [System.IO.Path]::GetDirectoryName($oldPath).TrimEnd([char[]]@('\', '/'))
    if (-not [string]::Equals($oldParent, $releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a release outside '$releaseRoot': $oldPath"
    }
    if ([string]::Equals($oldPath, $activeRelease, [StringComparison]::OrdinalIgnoreCase) -or
        ($oldRelease.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        Write-Warning "Skipped active or linked release: $oldPath"
        continue
    }
    try {
        Remove-Item -LiteralPath $oldPath -Recurse -Force -Confirm:$false -ErrorAction Stop
    }
    catch {
        # A locked historical release does not invalidate the already running, health-checked deployment.
        Write-Warning "Old release cleanup skipped for '$oldPath': $($_.Exception.Message)"
    }
}

Write-Host "Deployed $Branch to $releasePath"
