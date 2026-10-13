[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SiteName = 'ToDo2',
    [string]$SiteRoot = 'C:\inetpub\ToDo2'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ToDoRuntimeData.ps1')
Import-Module WebAdministration
$site = Get-Website -Name $SiteName -ErrorAction Stop
$release = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $SiteRoot 'releases')).TrimEnd([char[]]@('\','/'))
if (!$release.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The active site is outside the expected release directory: $release"
}
Assert-ToDoRuntimeDirectory $releaseRoot
Write-Host "Active IIS release: $release"
$result = Repair-ToDoRuntimeNesting -ReleasePath $release -WhatIf:$WhatIfPreference
$result | Format-List
Write-Host 'Recovery preserves all original directories and different existing files. It does not modify database records or restart the app pool.'
