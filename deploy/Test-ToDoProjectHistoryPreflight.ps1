#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$scriptPath=Join-Path $PSScriptRoot 'Complete-ToDoProjectHistory.ps1'
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($scriptPath,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'History wrapper has syntax errors'}
$source=[IO.File]::ReadAllText($scriptPath)
$start=$source.IndexOf('$SourcePath=(Resolve-Path',[StringComparison]::Ordinal)
$end=$source.IndexOf('$SiteRoot=[IO.Path]',[StringComparison]::Ordinal)
if($start -lt 0 -or $end -le $start){throw 'Cannot locate actual source preflight'}
# Execute the production statements, not a reimplementation. No IIS, database or package import is invoked.
$preflight=[scriptblock]::Create($source.Substring($start,$end-$start))
$connectionFunction=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-SiteConnection'},$true)
if(!$connectionFunction){throw 'Cannot locate actual connection resolver'}
Invoke-Expression $connectionFunction.Extent.Text
if(!$OutputDirectory){$OutputDirectory=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/history-preflight-'+[guid]::NewGuid().ToString('N'))}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new fixture directory'}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$repo=Join-Path $OutputDirectory 'source'
[IO.Directory]::CreateDirectory($repo)|Out-Null
function G([string[]]$Arguments){
    & git -C $repo @Arguments|Out-Null
    if($LASTEXITCODE -ne 0){throw 'Fixture git failed'}
}
function Write-Fixture([string]$Path,[string]$Content){[IO.File]::WriteAllText($Path,$Content,[Text.UTF8Encoding]::new($false))}
$script:checks=0
function Assert([bool]$Condition,[string]$Label){if(!$Condition){throw "FAIL: $Label"};$script:checks++;Write-Host "PASS: $Label"}
function Check-Source([bool]$ShouldPass,[string]$Label){
    $SourcePath=$repo
    $succeeded=$false
    try{& $preflight;$succeeded=$true}catch{if($_.Exception.Message -notmatch 'Git'){throw}}
    Assert ($succeeded -eq $ShouldPass) $Label
}
G @('init','--quiet')
Check-Source $true 'Clean empty repository under StrictMode Latest'
Write-Fixture (Join-Path $repo 'tracked.txt') 'baseline'
Check-Source $false 'One untracked file rejects'
Write-Fixture (Join-Path $repo 'another.txt') 'baseline'
Check-Source $false 'Multiple untracked files reject'
G @('add','tracked.txt','another.txt')
G @('-c','user.name=History Fixture','-c','user.email=history-fixture@example.invalid','commit','--quiet','-m','fixture')
Check-Source $true 'Clean committed repository under StrictMode Latest'
Write-Fixture (Join-Path $repo 'tracked.txt') 'changed'
Check-Source $false 'Unstaged tracked change rejects'
G @('add','tracked.txt')
Check-Source $false 'Staged tracked change rejects'
& {
    function git {$global:LASTEXITCODE=9}
    Check-Source $false 'Nonzero Git exit with no output rejects'
}
# Mock only the IIS provider; use the real filesystem for representative published web.config layouts.
$pool='fixture'
$script:poolVariables=@()
function Get-Item {
    param([string]$Path)
    if($Path -notlike 'IIS:*'){throw 'Unexpected provider access'}
    return [pscustomobject]@{environmentVariables=$script:poolVariables}
}
$originalConnection=$env:ConnectionStrings__DefaultConnection
try{
    $env:ConnectionStrings__DefaultConnection='unrelated-shell-value'
    Set-SiteConnection $repo
    Assert ([string]::IsNullOrEmpty($env:ConnectionStrings__DefaultConnection)) 'Missing web.config clears unrelated ambient override'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><system.webServer><aspNetCore /></system.webServer></configuration>'
    Set-SiteConnection $repo
    Assert ([string]::IsNullOrEmpty($env:ConnectionStrings__DefaultConnection)) 'Missing environmentVariables does not fail strict mode'
    $script:poolVariables=@([pscustomobject]@{name='ConnectionStrings__DefaultConnection';value='pool-fixture'})
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'pool-fixture') 'Pool override is preserved without web override'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="root-fixture" /></environmentVariables></aspNetCore></system.webServer></configuration>'
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'root-fixture') 'Root web.config override wins over pool'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><location path="."><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="location-fixture" /></environmentVariables></aspNetCore></system.webServer></location><location path="other"><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="wrong-scope" /></environmentVariables></aspNetCore></system.webServer></location></configuration>'
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'location-fixture') 'Published location wrapper is supported; child-location overrides excluded'
}finally{$env:ConnectionStrings__DefaultConnection=$originalConnection}
Write-Host "$script:checks checks passed on PowerShell $($PSVersionTable.PSVersion). No IIS or database changes. Fixtures: $OutputDirectory"
