#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tokens=$null;$parseErrors=$null
$wrapper=Join-Path $PSScriptRoot 'Complete-ToDoProjectHistory.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($wrapper,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Wrapper syntax errors'}
# Execute the whole production body; replace only external boundaries. Production still requires an administrator.
$historyWrapperScriptRoot=$PSScriptRoot
# An in-memory scriptblock has no PSScriptRoot; preserve only that source-file locator explicitly.
$bodyText=(@($ast.EndBlock.Statements|ForEach-Object{$_.Extent.Text}) -join "`r`n").Replace('$PSScriptRoot','$historyWrapperScriptRoot')
$body=[scriptblock]::Create($bodyText)
$fixtureRoot=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/history-workflow-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot)|Out-Null
function Write-Fixture([string]$Path,[string]$Content){
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent))|Out-Null
    [IO.File]::WriteAllText($Path,$Content,[Text.UTF8Encoding]::new($false))
}
$fixtureSource=Join-Path $fixtureRoot 'source'
Write-Fixture (Join-Path $fixtureSource 'deploy/Deploy-ToDo-Iis.ps1') 'param($SourcePath,$SiteName,$AppPoolName,$SiteRoot,$HealthCheckUrl,$HealthCheckHost) Invoke-FixtureDeployment'
foreach($gitArgs in @(@('init','--quiet'),@('add','.'),@('-c','user.name=Workflow Fixture','-c','user.email=workflow@example.invalid','commit','--quiet','-m','fixture'))){
    & git -C $fixtureSource @gitArgs
    if($LASTEXITCODE -ne 0){throw 'Fixture git failed'}
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipSource=Join-Path $fixtureRoot 'zip-source'
Write-Fixture (Join-Path $zipSource 'manifest.json') '{}'
$zipPath=Join-Path $fixtureRoot 'package.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($zipSource,$zipPath)
$fakeDump=Join-Path $fixtureRoot 'mysqldump-fixture.exe'
Write-Fixture $fakeDump 'Never executed'
$global:ToDoHistoryWorkflowState=[pscustomobject]@{Events=$null;PoolState='Started';CurrentRelease='';NextRelease='';FailureStage=''}
function Import-Module {param([string]$Name) if($Name -ne 'WebAdministration'){throw 'Unexpected module import'}}
function Get-Website {
    param([string]$Name)
    [pscustomobject]@{PhysicalPath=$global:ToDoHistoryWorkflowState.CurrentRelease;ApplicationPool='fixture-pool'}
}
function Get-WebAppPoolState {param([string]$Name) [pscustomobject]@{Value=$global:ToDoHistoryWorkflowState.PoolState}}
function Stop-WebAppPool {param([string]$Name) [void]$global:ToDoHistoryWorkflowState.Events.Add('Stop');$global:ToDoHistoryWorkflowState.PoolState='Stopped'}
function Start-WebAppPool {param([string]$Name) [void]$global:ToDoHistoryWorkflowState.Events.Add('Start');$global:ToDoHistoryWorkflowState.PoolState='Started'}
function Get-Acl {
    param([string]$LiteralPath)
    $acl=[pscustomobject]@{}
    $acl|Add-Member ScriptMethod SetAccessRuleProtection {param($Protected,$Preserve)}
    $acl|Add-Member ScriptMethod AddAccessRule {param($Rule)}
    return $acl
}
function Set-Acl {param([string]$LiteralPath,$AclObject)}
function Get-WebConfigurationProperty {
    [CmdletBinding()]
    param([string]$PSPath,[string]$Filter,[string]$Name)
    if($global:ToDoHistoryWorkflowState.FailureStage -eq 'IIS'){throw 'Fixture IIS read denied'}
    if($PSPath -ne 'MACHINE/WEBROOT/APPHOST' -or $Name -ne 'value'){throw 'Unexpected IIS configuration query'}
    # ConfigurationAttribute-shaped result has Value, no name property.
    [pscustomobject]@{Value='fixture-not-a-real-connection'}
}
function Invoke-FixtureDeployment {
    [void]$global:ToDoHistoryWorkflowState.Events.Add('Deploy')
    if($global:ToDoHistoryWorkflowState.FailureStage -eq 'Deploy'){throw 'Fixture deployment failed'}
    $global:ToDoHistoryWorkflowState.CurrentRelease=$global:ToDoHistoryWorkflowState.NextRelease
    $global:ToDoHistoryWorkflowState.PoolState='Started'
}
function dotnet {
    $arguments=@($args)
    $global:LASTEXITCODE=0
    if($arguments[0] -eq 'publish'){return}
    $stage='Preview'
    if($arguments -contains '--backup-output'){$stage='Backup'}
    if($arguments -contains '--apply'){$stage='Apply'}
    [void]$global:ToDoHistoryWorkflowState.Events.Add($stage)
    if($global:ToDoHistoryWorkflowState.FailureStage -eq $stage){$global:LASTEXITCODE=17;return}
    if($stage -eq 'Backup'){
        $path=$arguments[[array]::IndexOf($arguments,'--backup-output')+1]
        Write-Fixture $path ('fixture-backup '*20)
    }
    if($stage -eq 'Apply'){
        $path=$arguments[[array]::IndexOf($arguments,'--report')+1]
        Write-Fixture $path '{"Result":{"ProjectId":321}}'
    }
}
$script:checks=0
function Run-Case([string]$Label,[bool]$DoApply,[bool]$DoDeploy,[string]$Fail,[string]$ExpectedEvents){
    $caseRoot=Join-Path $fixtureRoot $Label
    $SiteName='fixture-site';$SiteRoot=$caseRoot;$SourcePath=$fixtureSource;$PackageZip=$zipPath
    $ActorUserName='fixture';$MySqlDumpPath=$fakeDump;$BaseUrl='https://example.invalid'
    $Apply=$DoApply;$Deploy=$DoDeploy
    $global:ToDoHistoryWorkflowState.CurrentRelease=Join-Path $caseRoot 'releases/previous'
    $global:ToDoHistoryWorkflowState.NextRelease=Join-Path $caseRoot 'releases/next'
    foreach($release in @($global:ToDoHistoryWorkflowState.CurrentRelease,$global:ToDoHistoryWorkflowState.NextRelease)){
        Write-Fixture (Join-Path $release 'web.config') '<configuration><location path="."><system.webServer><aspNetCore /></system.webServer></location></configuration>'
        Write-Fixture (Join-Path $release 'App_Data/fixture.txt') 'runtime fixture'
    }
    $global:ToDoHistoryWorkflowState.Events=New-Object 'Collections.Generic.List[string]'
    $global:ToDoHistoryWorkflowState.PoolState='Started';$global:ToDoHistoryWorkflowState.FailureStage=$Fail
    $before='ambient-fixture'
    $env:ConnectionStrings__DefaultConnection=$before
    $failure=$null
    try{& $body}catch{$failure=$_}
    if(($null -ne $failure) -ne (![string]::IsNullOrEmpty($Fail))){throw "FAIL: $Label unexpected outcome: $failure"}
    if($failure -and $failure.FullyQualifiedErrorId -match 'PropertyNotFoundStrict|VariableIsUndefined'){throw $failure}
    $actual=$global:ToDoHistoryWorkflowState.Events -join ','
    if($actual -ne $ExpectedEvents){throw "FAIL: $Label events $actual"}
    if($global:ToDoHistoryWorkflowState.PoolState -ne 'Started'){throw "FAIL: $Label left pool stopped"}
    if($env:ConnectionStrings__DefaultConnection -ne $before){throw "FAIL: $Label did not restore shell environment"}
    $script:checks++
    Write-Host "PASS: $Label ($actual); shell restored; fixture pool started"
}
$originalConnection=$env:ConnectionStrings__DefaultConnection
try{
    Run-Case 'preview-only' $false $false '' 'Preview'
    Run-Case 'apply-without-deploy' $true $false '' 'Preview,Stop,Backup,Apply,Start'
    Run-Case 'deploy-and-apply' $true $true '' 'Preview,Stop,Backup,Deploy,Stop,Apply,Start'
    Run-Case 'iis-read-failure' $true $true 'IIS' ''
    Run-Case 'preview-failure' $true $true 'Preview' 'Preview'
    Run-Case 'backup-failure' $true $true 'Backup' 'Preview,Stop,Backup,Start'
    Run-Case 'deployment-failure' $true $true 'Deploy' 'Preview,Stop,Backup,Deploy,Start'
    Run-Case 'apply-failure' $true $true 'Apply' 'Preview,Stop,Backup,Deploy,Stop,Apply,Start'
}finally{$env:ConnectionStrings__DefaultConnection=$originalConnection}
Write-Host "$script:checks simulated workflows passed on PowerShell $($PSVersionTable.PSVersion). No real IIS, database, dotnet publish or deployment was invoked. Fixtures: $fixtureRoot"
