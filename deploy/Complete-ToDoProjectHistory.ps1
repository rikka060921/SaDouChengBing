#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SiteName='ToDo2',
    [string]$SiteRoot='C:\inetpub\ToDo2',
    [string]$SourcePath='C:\Deploy\ToDo-source',
    [Parameter(Mandatory)][string]$PackageZip,
    [string]$ActorUserName='hongenze',
    [string]$MySqlDumpPath,
    [string]$BaseUrl='https://test1.icode8.net',
    [switch]$Deploy,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
Import-Module WebAdministration
. (Join-Path $PSScriptRoot 'ToDoRuntimeData.ps1')
$SourcePath=(Resolve-Path -LiteralPath $SourcePath).Path
$sourceChanges=@(& git -C $SourcePath status --porcelain --untracked-files=all)
$gitStatusExitCode=$LASTEXITCODE
if($gitStatusExitCode -ne 0 -or $sourceChanges.Count -gt 0){throw '源码目录不干净或 Git 检查失败，请先保留并处理本地修改；未更改站点或数据库'}
$SiteRoot=[IO.Path]::GetFullPath($SiteRoot).TrimEnd('\')
$packageFile=Get-Item -LiteralPath $PackageZip -ErrorAction Stop
$site=Get-Website -Name $SiteName -ErrorAction Stop
$previousRelease=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath))
if(!$previousRelease.StartsWith($SiteRoot+'\releases\',[StringComparison]::OrdinalIgnoreCase)){throw '站点当前目录不在指定 releases 下，停止以防操作错误站点'}
$pool=[string]$site.ApplicationPool
if(@(Get-Website|Where-Object{$_.ApplicationPool -eq $pool}).Count -ne 1){throw '应用池被多个站点共用，请先隔离，不能影响其他站点'}
$work=Join-Path $SiteRoot ('maintenance-backups\history-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($work)|Out-Null
# SQL and runtime backups must not be readable by the web application identity or served as static files.
$acl=Get-Acl -LiteralPath $work;$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @('S-1-5-32-544','S-1-5-18')){$identity=[Security.Principal.SecurityIdentifier]::new($sid);$rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow');$acl.AddAccessRule($rule)}
Set-Acl -LiteralPath $work -AclObject $acl
$package=Join-Path $work 'package';[IO.Directory]::CreateDirectory($package)|Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($packageFile.FullName)
try{
    if($archive.Entries.Count -gt 300 -or ($archive.Entries|Measure-Object Length -Sum).Sum -gt 200MB){throw '补录包数量或解压后大小异常'}
    foreach($entry in $archive.Entries){if($entry.FullName -notmatch '^(manifest\.json|files/([A-F0-9]{64}\.bin)?)$'){throw '补录 ZIP 含不允许的路径，停止解压'}}
}finally{$archive.Dispose()}
[IO.Compression.ZipFile]::ExtractToDirectory($packageFile.FullName,$package)
if(!(Test-Path -LiteralPath (Join-Path $package 'manifest.json'))){throw '补录包缺少 manifest.json'}
$toolOutput=Join-Path $work 'tool'
& dotnet publish (Join-Path $SourcePath 'tools\ToDo.HistoryBackfill\ToDo.HistoryBackfill.csproj') -c Release -o $toolOutput --nologo
if($LASTEXITCODE -ne 0){throw '补录工具编译失败，未更改站点和数据库'}
$tool=Join-Path $toolOutput 'ToDo.HistoryBackfill.dll'
$originalConnection=$env:ConnectionStrings__DefaultConnection
function ConvertTo-IisXPathLiteral([string]$Value){
    if(!$Value.Contains("'")){return "'"+$Value+"'"}
    if(!$Value.Contains('"')){return '"'+$Value+'"'}
    $parts=@($Value.Split([char]39)|ForEach-Object{"'"+$_+"'"})
    return 'concat('+($parts -join ', "''", ')+')'
}
function Set-SiteConnection([string]$Root){
    $script:siteConnection=$null
    # IIS exposes environmentVariables as a configuration container, not an array of name/value objects.
    # Query the specific attribute; do not enumerate the provider's container or ignore read errors.
    $poolLiteral=ConvertTo-IisXPathLiteral $pool
    $filter="system.applicationHost/applicationPools/add[@name=$poolLiteral]/environmentVariables/add[@name='ConnectionStrings__DefaultConnection']"
    $poolAttributes=@(Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name 'value' -ErrorAction Stop)
    if($poolAttributes.Count -gt 1){throw 'IIS connection override is ambiguous; import was not started.'}
    if($poolAttributes.Count -eq 1){
        $valueProperty=$poolAttributes[0].PSObject.Properties['Value']
        if($null -eq $valueProperty){throw 'Unexpected IIS configuration attribute shape; import was not started.'}
        $script:siteConnection=[string]$valueProperty.Value
    }
    $configFile=Join-Path $Root 'web.config'
    if(Test-Path -LiteralPath $configFile){
        [xml]$config=[IO.File]::ReadAllText($configFile)
        # Published ASP.NET Core web.config may wrap system.webServer in location; absent overrides are valid.
        foreach($item in $config.SelectNodes('/configuration/system.webServer/aspNetCore/environmentVariables/environmentVariable | /configuration/location[@path="."]/system.webServer/aspNetCore/environmentVariables/environmentVariable')){
            if($item.GetAttribute('name') -eq 'ConnectionStrings__DefaultConnection'){$script:siteConnection=$item.GetAttribute('value')}
        }
    }
    # When no IIS override exists, the tool reads appsettings.Production.json; never inherit an unrelated shell DB override.
    $env:ConnectionStrings__DefaultConnection=$script:siteConnection
}
function Run-Tool([string]$Root,[string[]]$Extra){
    Set-SiteConnection $Root
    & dotnet $tool --app-root $Root --package $package --actor $ActorUserName @Extra
    if($LASTEXITCODE -ne 0){throw '补录工具返回失败。请查看上方结果和本次备份；不自动恢复整库或删除原件。'}
}
function Wait-Pool([string]$State){
    $deadline=(Get-Date).AddSeconds(60)
    while((Get-WebAppPoolState -Name $pool).Value -ne $State){if((Get-Date) -gt $deadline){throw "应用池未达到 $State 状态"};Start-Sleep -Milliseconds 400}
}
$wasRunning=(Get-WebAppPoolState -Name $pool).Value -eq 'Started'
$stopped=$false
try{
    Write-Host 'History stage: preview (no database writes or pool stop yet).'
    Run-Tool $previousRelease @()
    if(!$Apply){Write-Host 'Preview only. No database records, app-pool state or IIS release were changed. Use -Apply to back up and import, optionally -Deploy.';return}
    if(!$MySqlDumpPath){$command=Get-Command mysqldump.exe -ErrorAction SilentlyContinue;if($command){$MySqlDumpPath=$command.Source}else{$candidate=Get-ChildItem -Path (Join-Path $env:ProgramFiles 'MySQL') -Filter mysqldump.exe -Recurse -File -ErrorAction SilentlyContinue|Select-Object -First 1;if($candidate){$MySqlDumpPath=$candidate.FullName}}}
    if(!$MySqlDumpPath -or !(Test-Path -LiteralPath $MySqlDumpPath)){throw '找不到 mysqldump.exe，请用 -MySqlDumpPath 指定本机备份工具；尚未停止站点或补录'}
    Write-Host 'History stage: stop target pool and back up database/runtime files.'
    if((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped'){$stopped=$true;Stop-WebAppPool -Name $pool;Wait-Pool 'Stopped'}
    $backup=Join-Path $work 'database-before.sql'
    Run-Tool $previousRelease @('--backup-output',$backup,'--mysql-dump',$MySqlDumpPath)
    foreach($relative in @('App_Data','wwwroot\uploads')){$source=Join-Path $previousRelease $relative;if(Test-Path -LiteralPath $source -PathType Container){Copy-ToDoRuntimeDirectory -Source $source -Destination (Join-Path (Join-Path $work 'runtime') $relative)|Out-Null}}
    Copy-Item -LiteralPath (Join-Path $previousRelease 'web.config') -Destination (Join-Path $work 'previous-web.config')
    @{Site=$SiteName;PreviousRelease=$previousRelease;DatabaseBackup=$backup;PackageSHA256=(Get-FileHash -LiteralPath $packageFile.FullName -Algorithm SHA256).Hash;RecordedAt=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $work 'backup-receipt.json') -Encoding UTF8
    if($Deploy){Write-Host 'History stage: deploy target IIS site.';& (Join-Path $SourcePath 'deploy\Deploy-ToDo-Iis.ps1') -SourcePath $SourcePath -SiteName $SiteName -AppPoolName $pool -SiteRoot $SiteRoot -HealthCheckUrl 'http://127.0.0.1/health/ready' -HealthCheckHost ([uri]$BaseUrl).Host}
    $current=[IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string](Get-Website -Name $SiteName).PhysicalPath))
    if(!$current.StartsWith($SiteRoot+'\releases\',[StringComparison]::OrdinalIgnoreCase)){throw '部署后的目录不在目标站点 releases 下，停止补录'}
    if((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped'){$stopped=$true;Stop-WebAppPool -Name $pool;Wait-Pool 'Stopped'}
    $report=Join-Path $work 'import-result.json'
    Write-Host 'History stage: apply history package (backup completed).'
    Run-Tool $current @('--apply','--backup',$backup,'--report',$report)
    $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    Write-Host "Local import verified. Project ID: $($result.Result.ProjectId). Backup and report: $work"
    Write-Host "$($BaseUrl.TrimEnd('/'))/Projects/Development?projectId=$($result.Result.ProjectId)"
    Write-Host 'Current tasks, meetings, accounts and old project records were preserved. Missing accounts were not created or enabled.'
}finally{
    $env:ConnectionStrings__DefaultConnection=$originalConnection
    if($stopped -and ($wasRunning -or $Deploy)){if((Get-WebAppPoolState -Name $pool).Value -ne 'Started'){Start-WebAppPool -Name $pool;Wait-Pool 'Started'}}
}
