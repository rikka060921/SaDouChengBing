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
foreach($functionName in @('ConvertTo-IisXPathLiteral','Set-SiteConnection')){
    $connectionFunction=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName},$true)
    if(!$connectionFunction){throw 'Cannot locate actual connection resolver'}
    Invoke-Expression $connectionFunction.Extent.Text
}
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
$script:poolXml=[xml]'<configuration><system.applicationHost><applicationPools><add name="fixture"><environmentVariables /></add><add name="other"><environmentVariables><add name="ConnectionStrings__DefaultConnection" value="wrong-pool" /></environmentVariables></add></applicationPools></system.applicationHost></configuration>'
$script:poolReadFails=$false
$script:malformedAttribute=$false
$script:duplicateAttribute=$false
$script:legacyProviderReads=0
function Get-Item {
    param([string]$Path)
    if($Path -notlike 'IIS:*'){throw 'Unexpected provider access'}
    $script:legacyProviderReads++
    # Reproduce the provider's container: no name/value properties on environmentVariables itself.
    return [pscustomobject]@{environmentVariables=[pscustomobject]@{Collection=@()}}
}
function Get-WebConfigurationProperty {
    [CmdletBinding()]
    param([string]$PSPath,[string]$Filter,[string]$Name)
    if($PSPath -ne 'MACHINE/WEBROOT/APPHOST' -or $Name -ne 'value' -or $ErrorActionPreference -ne 'Stop'){throw 'Unexpected configuration query'}
    if($script:poolReadFails){throw 'Fixture IIS configuration access denied'}
    if($script:malformedAttribute){return [pscustomobject]@{Unexpected='shape'}}
    if($script:duplicateAttribute){return @([pscustomobject]@{Value='first'},[pscustomobject]@{Value='second'})}
    # Actual cmdlet returns configuration attributes with Value, not environment entries with name.
    foreach($item in $script:poolXml.SelectNodes('/configuration/'+$Filter)){
        [pscustomobject]@{Value=$item.GetAttribute($Name)}
    }
}
function Expect-ConnectionFailure([string]$Pattern,[string]$Label){
    $message=''
    try{Set-SiteConnection $repo}catch{$message=$_.Exception.Message}
    Assert ($message -match $Pattern) $Label
}
$originalConnection=$env:ConnectionStrings__DefaultConnection
try{
    $env:ConnectionStrings__DefaultConnection='unrelated-shell-value'
    Set-SiteConnection $repo
    Assert ([string]::IsNullOrEmpty($env:ConnectionStrings__DefaultConnection)) 'Missing web.config clears unrelated ambient override'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><system.webServer><aspNetCore /></system.webServer></configuration>'
    Set-SiteConnection $repo
    Assert ([string]::IsNullOrEmpty($env:ConnectionStrings__DefaultConnection)) 'Missing environmentVariables does not fail strict mode'
    $script:poolXml=[xml]'<configuration><system.applicationHost><applicationPools><add name="fixture"><environmentVariables><add name="Unrelated" value="not-a-connection" /><add name="ConnectionStrings__DefaultConnection" value="pool-fixture" /></environmentVariables></add><add name="other"><environmentVariables><add name="ConnectionStrings__DefaultConnection" value="wrong-pool" /></environmentVariables></add></applicationPools></system.applicationHost></configuration>'
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'pool-fixture') 'Pool override is preserved without web override'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="root-fixture" /></environmentVariables></aspNetCore></system.webServer></configuration>'
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'root-fixture') 'Root web.config override wins over pool'
    Write-Fixture (Join-Path $repo 'web.config') '<configuration><location path="."><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="location-fixture" /></environmentVariables></aspNetCore></system.webServer></location><location path="other"><system.webServer><aspNetCore><environmentVariables><environmentVariable name="ConnectionStrings__DefaultConnection" value="wrong-scope" /></environmentVariables></aspNetCore></system.webServer></location></configuration>'
    Set-SiteConnection $repo
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'location-fixture') 'Published location wrapper is supported; child-location overrides excluded'
    Assert ($script:legacyProviderReads -eq 0) 'Does not enumerate provider environmentVariables container'
    $script:poolReadFails=$true
    Expect-ConnectionFailure 'access denied' 'IIS read failure stops even if web.config has override'
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'location-fixture') 'Failed lookup does not replace process connection with fallback'
    $script:poolReadFails=$false
    $script:malformedAttribute=$true
    Expect-ConnectionFailure 'Unexpected IIS configuration attribute shape' 'Malformed IIS attribute fails closed'
    $script:malformedAttribute=$false
    $script:duplicateAttribute=$true
    Expect-ConnectionFailure 'ambiguous' 'Multiple matches fail closed'
    $script:duplicateAttribute=$false
    [xml]$names='<pools />'
    foreach($testName in @('plain',"owner's pool",'owner"pool',('owner'+[char]39+[char]34+'pool'))){
        $element=$names.CreateElement('pool');$element.SetAttribute('name',$testName);[void]$names.DocumentElement.AppendChild($element)
        $literal=ConvertTo-IisXPathLiteral $testName
        $matches=$names.SelectNodes('/pools/pool[@name='+$literal+']')
        Assert ($matches.Count -eq 1 -and $matches[0].GetAttribute('name') -ceq $testName) 'XPath literal matches exactly with quote characters'
    }
    $pool="owner's pool"
    $script:poolXml=[xml]'<configuration><system.applicationHost><applicationPools><add name="owner&apos;s pool"><environmentVariables><add name="ConnectionStrings__DefaultConnection" value="quoted-pool" /></environmentVariables></add></applicationPools></system.applicationHost></configuration>'
    $emptyRoot=Join-Path $OutputDirectory 'empty-root';[IO.Directory]::CreateDirectory($emptyRoot)|Out-Null
    Set-SiteConnection $emptyRoot
    Assert ($env:ConnectionStrings__DefaultConnection -eq 'quoted-pool') 'Quoted pool name resolves through exact configuration query'
    $reproduced=$false
    try{$legacy=Get-Item 'IIS:\AppPools\fixture';foreach($entry in $legacy.environmentVariables){$null=$entry.name}}catch{$reproduced=$_.FullyQualifiedErrorId -match 'PropertyNotFoundStrict'}
    Assert $reproduced 'Old container enumeration reproduces server PropertyNotFoundStrict'
}finally{$env:ConnectionStrings__DefaultConnection=$originalConnection}
Write-Host "$script:checks checks passed on PowerShell $($PSVersionTable.PSVersion). No IIS or database changes. Fixtures: $OutputDirectory"
