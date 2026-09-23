[CmdletBinding()]
param(
    [string]$SiteName = 'ToDo2'
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$site = Get-Website -Name $SiteName -ErrorAction Stop
$webConfigPath = Join-Path ([string]$site.PhysicalPath) 'web.config'
if (-not (Test-Path $webConfigPath)) {
    throw "web.config was not found at $webConfigPath"
}

function Read-Secret {
    param([string]$Prompt)

    $secureValue = Read-Host $Prompt -AsSecureString
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

$dbPassword = Read-Secret 'ToDo production MySQL password'
$aiKey = Read-Secret 'DeepSeek API key'
$allowedHosts = Read-Host 'Allowed host names separated by semicolons (default: test1.icode8.net)'
$proxyIp = Read-Host 'Trusted HTTPS gateway IP (leave blank when IIS terminates HTTPS)'

if ([string]::IsNullOrWhiteSpace($allowedHosts)) {
    $allowedHosts = 'test1.icode8.net'
}

[xml]$webConfig = Get-Content $webConfigPath -Raw
$aspNetCore = $webConfig.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
if ($null -eq $aspNetCore) {
    $aspNetCore = $webConfig.SelectSingleNode('/configuration/system.webServer/aspNetCore')
}
if ($null -eq $aspNetCore) {
    throw 'The web.config does not contain an aspNetCore element.'
}

$environmentVariables = $aspNetCore.environmentVariables
if ($null -eq $environmentVariables) {
    $environmentVariables = $webConfig.CreateElement('environmentVariables')
    [void]$aspNetCore.AppendChild($environmentVariables)
}

function Set-EnvironmentVariable {
    param([string]$Name, [string]$Value)
    $element = @($environmentVariables.environmentVariable) |
        Where-Object { $_.name -eq $Name } |
        Select-Object -First 1
    if ($null -eq $element) {
        $element = $webConfig.CreateElement('environmentVariable')
        [void]$environmentVariables.AppendChild($element)
    }
    $element.SetAttribute('name', $Name)
    $element.SetAttribute('value', $Value)
}

Set-EnvironmentVariable 'ASPNETCORE_ENVIRONMENT' 'Production'
Set-EnvironmentVariable 'AllowedHosts' $allowedHosts
Set-EnvironmentVariable 'ConnectionStrings__DefaultConnection' "server=127.0.0.1;port=3306;database=todo_prod;user=todo_app;password=$dbPassword;CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True"
Set-EnvironmentVariable 'AI__ApiKey' $aiKey
Set-EnvironmentVariable 'AI__ApiUrl' 'https://api.deepseek.com/chat/completions'
Set-EnvironmentVariable 'AI__ModelName' 'deepseek-chat'
if (-not [string]::IsNullOrWhiteSpace($proxyIp)) {
    Set-EnvironmentVariable 'ForwardedHeaders__KnownProxies__0' $proxyIp
}

$webConfig.Save($webConfigPath)
Restart-WebAppPool -Name ([string]$site.ApplicationPool)
Remove-Variable dbPassword, aiKey, allowedHosts, proxyIp
Write-Host "ToDo environment configuration updated for IIS site '$SiteName'."
