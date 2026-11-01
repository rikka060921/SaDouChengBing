#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourcePath=(Split-Path $PSScriptRoot -Parent),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PriorEvidenceDirectory,
    [string]$ProjectName='撒豆成兵（正式开发）'
)
$ErrorActionPreference='Stop'
$SourcePath=(Resolve-Path -LiteralPath $SourcePath).Path
if(!(Test-Path -LiteralPath (Join-Path $SourcePath 'ToDo.sln'))){throw '不是项目根目录'}
if(!$PriorEvidenceDirectory){$PriorEvidenceDirectory=Join-Path $SourcePath 'artifacts/project-history-onboarding-20261012'}
if(!(Test-Path -LiteralPath (Join-Path $PriorEvidenceDirectory 'project-history-archive.json'))){throw '缺少经过核查的旧档案，停止生成不完整补录包'}
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $SourcePath 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(!$outputRoot.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw '私有补录包必须写入被忽略的 artifacts 目录，不能进入源码或公开仓库'}
[IO.Directory]::CreateDirectory((Join-Path $outputRoot 'files'))|Out-Null
$utf8=[Text.UTF8Encoding]::new($false)
function Save-Data([string]$Path,$Data){[IO.File]::WriteAllText($Path,($Data|ConvertTo-Json -Depth 30),$utf8)}
function Excerpt([string]$Value,[int]$Limit){
    $clean=[regex]::Replace($Value,'(?i)(?:tvly-(?:dev-)?[A-Za-z0-9_-]{20,}|github_pat_[A-Za-z0-9_]{20,}|ghp_[A-Za-z0-9]{20,}|sk-proj-[A-Za-z0-9_-]{20,})','[REDACTED_SECRET]')
    $clean=[regex]::Replace($clean,'(?i)((?:password|pwd|apikey|api_key)\s*[:=]\s*)[^\s;"'']+','$1[REDACTED_VALUE]')
    $clean=[regex]::Replace($clean,'(密码\s*[:：=]\s*)[^\s;，。"'']+','$1[REDACTED_VALUE]')
    if($clean.Length -gt $Limit){return $clean.Substring(0,$Limit-10)+'…（安全摘录）'}
    return $clean.Trim()
}
$head=(& git -C $SourcePath rev-parse HEAD).Trim();if($LASTEXITCODE -ne 0){throw '无法读取 Git HEAD'}
$raw=(& git -c core.quotepath=false -C $SourcePath log HEAD '--format=%x1e%H%x1f%an%x1f%aI%x1f%cI%x1f%s%x1f%b%x00' --name-only --root) -join "`n"
if($LASTEXITCODE -ne 0){throw '读取 Git 历史失败'}
$changes=[Collections.Generic.List[object]]::new()
foreach($chunk in $raw.Split([char]0x1e,[StringSplitOptions]::RemoveEmptyEntries)){
    $pieces=$chunk.Split([char]0,2);$fields=$pieces[0].Trim().Split([char]0x1f,6)
    if($fields.Count -ne 6 -or $fields[0] -notmatch '^[a-f0-9]{40}$'){throw 'Git 历史格式异常'}
    $files=@();if($pieces.Count -gt 1){$files=@($pieces[1].Split("`n")|ForEach-Object{$_.Trim()}|Where-Object{$_}|Select-Object -Unique)}
    $author=[regex]::Replace($fields[1],'\([^)]*@[^)]*\)','').Trim()
    $changes.Add([ordered]@{Commit=$fields[0];Author=(Excerpt $author 160);AuthoredAt=$fields[2];CommittedAt=$fields[3];Subject=(Excerpt $fields[4] 1000);Message=(Excerpt $fields[5] 8000);FileCount=$files.Count;Files=@($files|Select-Object -First 100|ForEach-Object{Excerpt $_ 1000})})
}
$expectedCount=[int](& git -C $SourcePath rev-list --count HEAD)
if($changes.Count -ne $expectedCount){throw '提交数量不一致，停止导出'}
$participants=@(
    [ordered]@{Name='洪恩泽';UserName='hongenze';Responsibility='项目负责人；总体协调、Agent 与部署验收（依据已确认团队安排，不倒推单次提交归属）'},
    [ordered]@{Name='赵珂薇';UserName='zhaokewei';Responsibility='老成员；会议、任务与协作流程开发（具体贡献以 Git 署名为准）'},
    [ordered]@{Name='贺楚';UserName='hechu';Responsibility='老成员；既有技术开发工作（Git 别名 hc 未强行映射为已核实身份）'},
    [ordered]@{Name='郭彩丽';UserName='guocaili';Responsibility='新成员；熟悉业务、试用、资料、反馈与验收，不预设代码任务'}
)
$ledger=[ordered]@{FormatVersion=1;ProjectId=0;SourceHead=$head;CapturedAt=[DateTimeOffset]::UtcNow.ToString('o');Participants=$participants;Changes=$changes.ToArray()}
$generatedLedger=Join-Path $outputRoot 'ledger-source.json';Save-Data $generatedLedger $ledger
$manifestFiles=[Collections.Generic.List[object]]::new();$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Source([string]$Path,[string]$Name,[string]$Category,[string]$Description){
    if(!$names.Add($Name)){return}
    if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw "来源文件缺失：$Name"}
    $bytes=[IO.File]::ReadAllBytes($Path)
    if($Name.EndsWith('.md') -or $Name.EndsWith('.txt')){
        $content=[Text.Encoding]::UTF8.GetString($bytes)
        if($content -match '(?:tvly-(?:dev-)?[A-Za-z0-9_-]{20,}|github_pat_[A-Za-z0-9_]{20,}|ghp_[A-Za-z0-9]{20,}|sk-proj-[A-Za-z0-9_-]{20,})'){throw "来源文件疑似含密钥，停止打包：$Name"}
    }
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes));$relative="files/$hash.bin"
    $destination=Join-Path $outputRoot $relative
    if(Test-Path -LiteralPath $destination){if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash){throw '已有补录文件校验失败'}}else{[IO.File]::WriteAllBytes($destination,$bytes)}
    $manifestFiles.Add([ordered]@{Path=$relative;Name=$Name;Category=$Category;Description=$Description;Hash=$hash})
}
Add-Source $generatedLedger 'project-development-ledger.json' '开发历史索引' "来自当前分支完整 Git 可达历史，来源 HEAD $head；安全摘录，不是旧系统审计日志，不据此授予任何账号权限。"
$oldManifest=Get-Content -LiteralPath (Join-Path $PriorEvidenceDirectory 'upload-manifest.json') -Raw|ConvertFrom-Json
foreach($item in $oldManifest){
    if($item.Name -eq '正式项目使用说明.md'){Add-Source $item.Path '历史_正式项目使用说明_20261013.md' '历史项目说明' '原站 2026-10-13 使用说明；文中的项目/资料编号仅适用于原站，不是本次目标编号。';continue}
    if([IO.Path]::GetFullPath($item.Path).StartsWith((Join-Path $SourcePath 'docs')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){continue}
    Add-Source $item.Path $item.Name $item.Category $item.Description
}
$docPaths=@(& git -c core.quotepath=false -C $SourcePath ls-files 'docs/*.md')
if($LASTEXITCODE -ne 0){throw '读取仓库资料索引失败'}
foreach($relative in @('README.md','CHANGELOG.md')+$docPaths){
    $file=Join-Path $SourcePath $relative
    if(!(Test-Path -LiteralPath $file)){throw "已跟踪的资料文件缺失：$relative"}
    Add-Source $file ([IO.Path]::GetFileName($file)) '开发与验收资料' "来源：仓库 $relative；整理时 HEAD $head，实际正文以 SHA256 为准。历史需求/设计不自动代表当前实现或验收结果。"
}
$usage=Join-Path $outputRoot 'usage-source.md'
[IO.File]::WriteAllText($usage,"# 撒豆成兵正式项目使用说明`n`n项目历程保留阶段节点；开发记录逐条展示 Git 署名、提交日期和修改说明；历史档案保存已核查的旧任务与会议，只读且不重新催办。项目资料保留原件、版本与来源。`n`n本次 Git 来源：$head，共 $($changes.Count) 条提交；人员按目标环境用户名匹配，不照搬数据库 ID，不自动创建账号、设置密码或开放权限。原站旧任务/会议链接不直接套用到本地，以免误跳到同号的其他项目数据。`n`n系统操作日志只记录实际发生的补录；Git 提交不能伪装为早年系统操作，AI 总结不能当作会议原话。`n`n当前四人参与信息来自已确认安排；缺失账号与未知 Git 别名保持待核实。早期任务执行细节、未保存的会议、未知日期不编造。服务器只有部署代码并导入此私有补录包后才会更新，推送 Git 不能同步数据库。",$utf8)
Add-Source $usage '正式项目使用说明.md' '项目说明' '本次补录入口、来源边界与缺失项说明；不复用其他环境的数据库编号。'
$timeline=Get-Content -LiteralPath (Join-Path $SourcePath 'deploy/SaDouChengBing-ProjectHistory.json') -Raw|ConvertFrom-Json
$manifest=[ordered]@{FormatVersion=1;ProjectName=$ProjectName;LeaderUserName='hongenze';Timeline=$timeline;Files=$manifestFiles.ToArray()}
Save-Data (Join-Path $outputRoot 'manifest.json') $manifest
[pscustomobject]@{Output=$outputRoot;SourceHead=$head;GitChanges=$changes.Count;GitAuthorNames=@($changes|ForEach-Object Author|Select-Object -Unique).Count;Milestones=$timeline.Count;Files=$manifestFiles.Count;Note='私有运行数据，不提交 Git；不主动打包配置、凭据或数据库备份，原始业务资料须按私有文件管理'}|ConvertTo-Json -Compress
