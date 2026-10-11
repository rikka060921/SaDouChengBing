[CmdletBinding()]
param(
    [string]$SiteName = 'ToDo2'
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$site = Get-Website -Name $SiteName -ErrorAction Stop
$sitePath = [Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath)
$webConfigPath = Join-Path $sitePath 'web.config'
if (-not (Test-Path -LiteralPath $webConfigPath -PathType Leaf)) {
    throw "web.config was not found for IIS site '$SiteName'."
}

$secureKey = Read-Host 'Tavily API key' -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
try {
    $apiKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    Remove-Variable secureKey -ErrorAction SilentlyContinue
}
if ([string]::IsNullOrWhiteSpace($apiKey) -or $apiKey.Length -gt 512 -or $apiKey -match '\s') {
    Remove-Variable apiKey -ErrorAction SilentlyContinue
    throw 'The Tavily API key is empty or has an invalid format.'
}

$temporaryPath = Join-Path $sitePath ("web.config.{0}.tmp" -f [guid]::NewGuid().ToString('N'))
$backupPath = Join-Path $sitePath ("web.config.search-backup-{0}.bak" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
try {
    [xml]$webConfig = Get-Content -LiteralPath $webConfigPath -Raw
    $aspNetCore = $webConfig.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
    if ($null -eq $aspNetCore) {
        $aspNetCore = $webConfig.SelectSingleNode('/configuration/system.webServer/aspNetCore')
    }
    if ($null -eq $aspNetCore) {
        throw 'The web.config does not contain an aspNetCore element.'
    }

    $environmentVariables = $aspNetCore.SelectSingleNode('environmentVariables')
    if ($null -eq $environmentVariables) {
        $environmentVariables = $webConfig.CreateElement('environmentVariables')
        [void]$aspNetCore.AppendChild($environmentVariables)
    }

    foreach ($entry in @(
        @{ Name = 'AgentWebSearch__Enabled'; Value = 'true' },
        @{ Name = 'AgentWebSearch__ApiKey'; Value = $apiKey }
    )) {
        $element = @($environmentVariables.SelectNodes('environmentVariable')) |
            Where-Object { $_.GetAttribute('name') -eq $entry.Name } |
            Select-Object -First 1
        if ($null -eq $element) {
            $element = $webConfig.CreateElement('environmentVariable')
            [void]$environmentVariables.AppendChild($element)
        }
        $element.SetAttribute('name', $entry.Name)
        $element.SetAttribute('value', $entry.Value)
    }

    $webConfig.Save($temporaryPath)
    [System.IO.File]::Replace($temporaryPath, $webConfigPath, $backupPath, $true)
    Restart-WebAppPool -Name ([string]$site.ApplicationPool)
    Write-Host "Tavily search enabled for IIS site '$SiteName'. Previous web.config: $backupPath"
}
finally {
    if (Test-Path -LiteralPath $temporaryPath) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
    Remove-Variable apiKey -ErrorAction SilentlyContinue
}
