[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNull()]
    [uri]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [ValidateNotNull()]
    [pscredential]$Credential,

    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$AgentKey = 'project-workbench',

    [ValidateRange(30, 900)]
    [int]$AgentTimeoutSeconds = 240,

    [ValidateRange(30, 600)]
    [int]$RequestTimeoutSeconds = 180,

    [ValidateRange(0, 2147483647)]
    [int]$InspectSessionId = 0,

    [switch]$SkipAgent,

    [switch]$ArchiveAfterRun,

    [switch]$DemoMode,

    # 仅对本次脚本创建的演示任务确认计划；默认仍在页面人工确认。
    [switch]$ConfirmAgentPlans,

    [string]$DemoDocumentPath,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$previousProgressPreference = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'

$script:BaseUri = [uri]($BaseUrl.AbsoluteUri.TrimEnd('/') + '/')
$script:WebSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$script:CreatedProjectId = $null
$script:ProjectArchived = $false
$runStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$randomSuffix = [guid]::NewGuid().ToString('N').Substring(0, 6)
$projectName = if ($DemoMode) {
    "星桥智造 · 华东20店智能巡检上线（演示 $(Get-Date -Format 'MMdd-HHmmss')）"
}
else {
    "E2E-Agent-$runStamp-$randomSuffix"
}

if ($DemoMode -and [string]::IsNullOrWhiteSpace($DemoDocumentPath)) {
    $DemoDocumentPath = Join-Path $PSScriptRoot '..\demo-assets\星桥智造智能巡检试点方案V1.0.docx'
}
if ($DemoMode) {
    $DemoDocumentPath = [System.IO.Path]::GetFullPath($DemoDocumentPath)
    if (-not (Test-Path -LiteralPath $DemoDocumentPath -PathType Leaf)) {
        throw "演示项目资料不存在：$DemoDocumentPath"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $resultDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'ToDo-Agent-E2E'
    $OutputPath = Join-Path $resultDirectory "$projectName.json"
}

$report = [ordered]@{
    StartedAt = (Get-Date).ToString('o')
    FinishedAt = $null
    Success = $false
    BaseUrl = $script:BaseUri.AbsoluteUri.TrimEnd('/')
    UserName = $Credential.UserName
    Mode = if ($DemoMode) { 'Demo' } else { 'E2E' }
    Project = $null
    Tasks = @()
    Meetings = @()
    Documents = @()
    Agent = $null
    ApprovalDemo = $null
    ArchivedAfterRun = $false
    Error = $null
}

function Write-Step {
    param([string]$Message)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Resolve-ToDoUri {
    param([Parameter(Mandatory = $true)][string]$Path)

    $absolute = $null
    if ([uri]::TryCreate($Path, [System.UriKind]::Absolute, [ref]$absolute)) {
        return $absolute
    }
    return [uri]::new($script:BaseUri, $Path.TrimStart('/'))
}

function Get-ResponseUri {
    param([Parameter(Mandatory = $true)]$Response)

    $requestMessageProperty = $Response.BaseResponse.PSObject.Properties['RequestMessage']
    if ($null -ne $requestMessageProperty -and
        $null -ne $requestMessageProperty.Value -and
        $null -ne $requestMessageProperty.Value.RequestUri) {
        return $requestMessageProperty.Value.RequestUri.AbsoluteUri
    }
    $responseUriProperty = $Response.BaseResponse.PSObject.Properties['ResponseUri']
    if ($null -ne $responseUriProperty -and $null -ne $responseUriProperty.Value) {
        return $responseUriProperty.Value.AbsoluteUri
    }
    return $Response.Headers['Location']
}

function Invoke-ToDoGet {
    param([Parameter(Mandatory = $true)][string]$Path)

    return Invoke-WebRequest `
        -Uri (Resolve-ToDoUri $Path) `
        -Method Get `
        -WebSession $script:WebSession `
        -UseBasicParsing `
        -MaximumRedirection 5 `
        -TimeoutSec $RequestTimeoutSeconds `
        -ErrorAction Stop
}

function Get-AntiForgeryToken {
    param([Parameter(Mandatory = $true)][string]$Html)

    $match = [regex]::Match(
        $Html,
        '(?is)<input\b[^>]*name="__RequestVerificationToken"[^>]*value="(?<value>[^"]+)"')
    if (-not $match.Success) {
        throw '页面中没有找到防伪令牌，可能被重定向到错误页面或登录已经失效。'
    }
    return [System.Net.WebUtility]::HtmlDecode($match.Groups['value'].Value)
}

function Assert-NotLoginPage {
    param([Parameter(Mandatory = $true)]$Response)

    $responseUri = Get-ResponseUri $Response
    if ($responseUri -match '/Account/Login') {
        throw "请求被重定向到登录页：$responseUri"
    }
}

function Invoke-ToDoFormPost {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Fields,
        [Parameter(Mandatory = $true)][string]$Token
    )

    $body = @{}
    foreach ($entry in $Fields.GetEnumerator()) {
        $body[$entry.Key] = $entry.Value
    }
    $body['__RequestVerificationToken'] = $Token

    $response = Invoke-WebRequest `
        -Uri (Resolve-ToDoUri $Path) `
        -Method Post `
        -Body $body `
        -ContentType 'application/x-www-form-urlencoded' `
        -WebSession $script:WebSession `
        -UseBasicParsing `
        -MaximumRedirection 5 `
        -TimeoutSec $RequestTimeoutSeconds `
        -ErrorAction Stop
    Assert-NotLoginPage $response
    return $response
}

function Invoke-ToDoMultipartPost {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Fields,
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string]$FileFieldName = 'UploadFile',
        [string]$UploadFileName = 'AgentDemoProjectBrief.docx'
    )

    $boundary = '--------------------------' + [guid]::NewGuid().ToString('N')
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $allFields = @{}
        foreach ($entry in $Fields.GetEnumerator()) {
            $allFields[$entry.Key] = $entry.Value
        }
        $allFields['__RequestVerificationToken'] = $Token

        foreach ($entry in $allFields.GetEnumerator()) {
            $fieldHeader = "--$boundary`r`nContent-Disposition: form-data; name=`"$($entry.Key)`"`r`n`r`n$($entry.Value)`r`n"
            $fieldBytes = $encoding.GetBytes($fieldHeader)
            $stream.Write($fieldBytes, 0, $fieldBytes.Length)
        }

        $fileHeader = "--$boundary`r`nContent-Disposition: form-data; name=`"$FileFieldName`"; filename=`"$UploadFileName`"`r`nContent-Type: application/vnd.openxmlformats-officedocument.wordprocessingml.document`r`n`r`n"
        $fileHeaderBytes = $encoding.GetBytes($fileHeader)
        $stream.Write($fileHeaderBytes, 0, $fileHeaderBytes.Length)
        $fileBytes = [System.IO.File]::ReadAllBytes($FilePath)
        $stream.Write($fileBytes, 0, $fileBytes.Length)
        $footerBytes = $encoding.GetBytes("`r`n--$boundary--`r`n")
        $stream.Write($footerBytes, 0, $footerBytes.Length)

        try {
            $response = Invoke-WebRequest `
                -Uri (Resolve-ToDoUri $Path) `
                -Method Post `
                -Body $stream.ToArray() `
                -ContentType "multipart/form-data; boundary=$boundary" `
                -WebSession $script:WebSession `
                -UseBasicParsing `
                -MaximumRedirection 0 `
                -TimeoutSec $RequestTimeoutSeconds `
                -ErrorAction Stop
        }
        catch {
            $failedRequest = $_.TargetObject
            if ($null -eq $failedRequest -or -not $failedRequest.HaveResponse) {
                throw
            }
            $redirectResponse = $failedRequest.GetResponse()
            try {
                if ([int]$redirectResponse.StatusCode -notin 301, 302, 303, 307, 308) {
                    throw
                }
                $location = $redirectResponse.Headers['Location']
                if ([string]::IsNullOrWhiteSpace($location)) {
                    throw '上传完成后服务器没有返回可跟随的跳转地址。'
                }
            }
            finally {
                $redirectResponse.Dispose()
            }
            $response = Invoke-ToDoGet $location
        }
        Assert-NotLoginPage $response
        return $response
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-ContainsText {
    param(
        [Parameter(Mandatory = $true)][string]$Html,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$StepName
    )

    # Razor 默认会把模型中的中文等字符编码为 HTML 实体；统一解码后再断言。
    $decodedHtml = [System.Net.WebUtility]::HtmlDecode($Html)
    if ($decodedHtml.IndexOf($Expected, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "$StepName 未在响应页面中找到预期内容：$Expected"
    }
}

function ConvertFrom-HtmlText {
    param([AllowEmptyString()][string]$Html)

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return ''
    }
    $decoded = [System.Net.WebUtility]::HtmlDecode($Html)
    $withoutTags = [regex]::Replace($decoded, '(?is)<[^>]+>', ' ')
    return [regex]::Replace($withoutTags, '\s+', ' ').Trim()
}

function Get-DocumentIndexStatus {
    param(
        [Parameter(Mandatory = $true)][string]$Html,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    foreach ($row in [regex]::Matches($Html, '(?is)<tr\b[^>]*>.*?</tr>')) {
        $decoded = [System.Net.WebUtility]::HtmlDecode($row.Value)
        if ($decoded.IndexOf($FileName, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            continue
        }
        foreach ($status in @('可检索', '索引失败', '暂不支持', '索引中', '待索引')) {
            if ($decoded.Contains($status)) {
                return $status
            }
        }
        return '未知'
    }
    return '未找到'
}

function Get-AgentSessionPageState {
    param([Parameter(Mandatory = $true)][string]$Html)

    $decoded = [System.Net.WebUtility]::HtmlDecode($Html)
    $sessionMatch = [regex]::Match(
        $decoded,
        '(?is)<h2[^>]*>.*?</h2>\s*<span[^>]*>(?<status>执行中|等待输入|已结束|已取消|失败)</span>')
    $jobMatch = [regex]::Match(
        $decoded,
        '(?is)<strong>后台运行 #(?<id>\d+)：</strong>\s*(?<status>[^·<]+?)\s*·\s*尝试\s*(?<attempt>\d+/\d+)\s*·\s*步骤\s*(?<steps>\d+/\d+)')
    $jobAlertMatch = [regex]::Match(
        $decoded,
        '(?is)<div class="alert[^"]*"[^>]*>\s*<div>\s*<strong>后台运行 #.*?</div>')
    $sessionErrorMatch = [regex]::Match(
        $decoded,
        '(?is)<div class="alert alert-danger"[^>]*>\s*<strong>执行失败：</strong>(?<error>.*?)</div>')

    return [pscustomobject]@{
        SessionStatus = if ($sessionMatch.Success) { $sessionMatch.Groups['status'].Value.Trim() } else { '未知' }
        RunJobId = if ($jobMatch.Success) { [long]$jobMatch.Groups['id'].Value } else { $null }
        RunJobStatus = if ($jobMatch.Success) { $jobMatch.Groups['status'].Value.Trim() } else { '未显示' }
        Attempt = if ($jobMatch.Success) { $jobMatch.Groups['attempt'].Value } else { '' }
        Steps = if ($jobMatch.Success) { $jobMatch.Groups['steps'].Value } else { '' }
        JobText = if ($jobAlertMatch.Success) { ConvertFrom-HtmlText $jobAlertMatch.Value } else { '' }
        SessionError = if ($sessionErrorMatch.Success) { ConvertFrom-HtmlText $sessionErrorMatch.Groups['error'].Value } else { '' }
    }
}

function Get-RecommendedDispatchAgentId {
    param([Parameter(Mandatory = $true)][string]$Html)

    $selectMatch = [regex]::Match(
        $Html,
        '(?is)<select\b[^>]*(?:id|name)="SelectedDispatchAgentId"[^>]*>(?<options>.*?)</select>')
    if (-not $selectMatch.Success) {
        throw '调度确认页中没有找到 Agent 候选列表。'
    }

    $options = $selectMatch.Groups['options'].Value
    $selectedMatch = [regex]::Match(
        $options,
        '(?is)<option\b(?=[^>]*\bvalue="(?<id>\d+)")(?=[^>]*\bselected(?:="[^"]*")?)[^>]*>')
    if ($selectedMatch.Success) {
        return [int]$selectedMatch.Groups['id'].Value
    }

    $firstMatch = [regex]::Match($options, '(?is)<option\b[^>]*\bvalue="(?<id>\d+)"[^>]*>')
    if (-not $firstMatch.Success) {
        throw '调度确认页中的 Agent 候选列表为空。'
    }
    return [int]$firstMatch.Groups['id'].Value
}

function Get-AgentTaskPageState {
    param([Parameter(Mandatory = $true)][string]$Html)

    $decoded = [System.Net.WebUtility]::HtmlDecode($Html)
    $agentStatusMatch = [regex]::Match(
        $decoded,
        '(?is)<span class="badge[^>]*>\s*(?<status>Agent待执行|Agent执行中|等待敏感操作审批|等待自动重试|待人工确认|已确认完成|执行失败|已暂停|等待确认 Agent 派单|等待确认执行计划)\s*</span>')
    $workItemMatch = [regex]::Match(
        $decoded,
        '(?is)<span class="small text-muted ms-1">#(?<id>\d+).*?</span>.*?<span class="badge[^>]*>\s*(?<status>待执行|执行中|等待审批|等待重试|已完成|失败|已取消|已暂停|待确认执行计划)\s*</span>.*?步骤\s*(?<steps>\d+/\d+)\s*·\s*尝试\s*(?<attempt>\d+/\d+)')
    $sessionMatch = [regex]::Match(
        $decoded,
        '(?i)/AiSessions/Details(?:\?id=|/)(?<id>\d+)')
    $errorMatch = [regex]::Match(
        $decoded,
        '(?is)<div class="small text-danger mt-1">(?<error>.*?)</div>')

    return [pscustomobject]@{
        AgentStatus = if ($agentStatusMatch.Success) { $agentStatusMatch.Groups['status'].Value.Trim() } else { '未知' }
        WorkItemId = if ($workItemMatch.Success) { [long]$workItemMatch.Groups['id'].Value } else { $null }
        WorkItemStatus = if ($workItemMatch.Success) { $workItemMatch.Groups['status'].Value.Trim() } else { '未创建' }
        Attempt = if ($workItemMatch.Success) { $workItemMatch.Groups['attempt'].Value } else { '' }
        Steps = if ($workItemMatch.Success) { $workItemMatch.Groups['steps'].Value } else { '' }
        SessionId = if ($sessionMatch.Success) { [long]$sessionMatch.Groups['id'].Value } else { $null }
        Error = if ($errorMatch.Success) { ConvertFrom-HtmlText $errorMatch.Groups['error'].Value } else { '' }
    }
}

function Confirm-DemoExecutionPlan {
    param(
        [Parameter(Mandatory = $true)][long]$TaskId,
        [Parameter(Mandatory = $true)]$TaskPage
    )
    $form = [regex]::Match($TaskPage.Content, '(?is)<form\b[^>]*action="[^"]*handler=ReviewPlan[^"]*"[^>]*>(?<fields>.*?)</form>')
    if (-not $form.Success) { throw "任务 #$TaskId 没有可确认的执行计划表单，请检查当前用户权限。" }
    $work = [regex]::Match($form.Groups['fields'].Value, '(?is)name="workItemId"[^>]*value="(?<value>\d+)"')
    $revision = [regex]::Match($form.Groups['fields'].Value, '(?is)name="revision"[^>]*value="(?<value>\d+)"')
    if (-not $work.Success -or -not $revision.Success) { throw '计划表单缺少工作项或版本，已停止自动确认。' }
    Write-Step "按显式 -ConfirmAgentPlans 参数确认本次演示任务 #$TaskId 的计划第 $($revision.Groups['value'].Value) 版"
    $page = Invoke-ToDoFormPost ("/Tasks/Details/{0}?handler=ReviewPlan" -f $TaskId) @{
        workItemId = $work.Groups['value'].Value
        revision = $revision.Groups['value'].Value
        approved = 'true'
        PlanFeedback = ''
    } (Get-AntiForgeryToken $form.Value)
    Assert-ContainsText $page.Content '计划已确认' '执行计划确认验收'
    return $page
}

function Get-ProjectIdFromList {
    param(
        [Parameter(Mandatory = $true)][string]$Html,
        [Parameter(Mandatory = $true)][string]$Name
    )

    foreach ($row in [regex]::Matches($Html, '(?is)<tr\b[^>]*data-project-id="(?<id>\d+)"[^>]*>.*?</tr>')) {
        $decoded = [System.Net.WebUtility]::HtmlDecode($row.Value)
        if ($decoded.IndexOf($Name, [System.StringComparison]::Ordinal) -ge 0) {
            return [int]$row.Groups['id'].Value
        }
    }
    throw "创建项目后无法从项目列表定位项目：$Name"
}

function Get-TaskIdFromList {
    param(
        [Parameter(Mandatory = $true)][string]$Html,
        [Parameter(Mandatory = $true)][string]$Title
    )

    foreach ($row in [regex]::Matches($Html, '(?is)<tr\b[^>]*>.*?</tr>')) {
        $decoded = [System.Net.WebUtility]::HtmlDecode($row.Value)
        if ($decoded.IndexOf($Title, [System.StringComparison]::Ordinal) -lt 0) {
            continue
        }
        $idMatch = [regex]::Match($row.Value, '(?i)/Tasks/Details/(?<id>\d+)')
        if ($idMatch.Success) {
            return [int]$idMatch.Groups['id'].Value
        }
    }
    throw "创建任务后无法从任务列表定位任务：$Title"
}

function Get-IdFromDetailsUri {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$EntityName
    )

    $match = [regex]::Match($Uri, '(?i)(?:[?&]id=|/Details/)(?<id>\d+)')
    if (-not $match.Success) {
        throw "$EntityName 创建后没有跳转到详情页：$Uri"
    }
    return [int]$match.Groups['id'].Value
}

function Save-Report {
    $report.FinishedAt = (Get-Date).ToString('o')
    $directory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}

function Archive-TestProject {
    if (-not $script:CreatedProjectId -or $script:ProjectArchived) {
        return
    }

    Write-Step "归档测试项目 #$script:CreatedProjectId"
    $indexPage = Invoke-ToDoGet '/Projects'
    Assert-NotLoginPage $indexPage
    $token = Get-AntiForgeryToken $indexPage.Content
    $response = Invoke-ToDoFormPost '/Projects?handler=Archive' @{
        projectId = $script:CreatedProjectId
        shouldArchive = 'true'
    } $token
    $result = $response.Content | ConvertFrom-Json
    if (-not $result.success) {
        throw "归档测试项目失败：$($result.message)"
    }
    $script:ProjectArchived = $true
    $report.ArchivedAfterRun = $true
}

try {
    Write-Step "登录 $($script:BaseUri.Host)（账号：$($Credential.UserName)）"
    $loginPage = Invoke-ToDoGet '/Account/Login'
    $loginToken = Get-AntiForgeryToken $loginPage.Content
    $password = $Credential.GetNetworkCredential().Password
    try {
        $loginResponse = Invoke-WebRequest `
            -Uri (Resolve-ToDoUri '/Account/Login') `
            -Method Post `
            -Body @{
                'Input.UserName' = $Credential.UserName
                'Input.Password' = $password
                'Input.RememberMe' = 'false'
                '__RequestVerificationToken' = $loginToken
            } `
            -ContentType 'application/x-www-form-urlencoded' `
            -WebSession $script:WebSession `
            -UseBasicParsing `
            -TimeoutSec $RequestTimeoutSeconds `
            -ErrorAction Stop
    }
    finally {
        $password = $null
    }
    Assert-NotLoginPage $loginResponse
    $projectsPage = Invoke-ToDoGet '/Projects'
    Assert-NotLoginPage $projectsPage

    if ($InspectSessionId -gt 0) {
        Write-Step "诊断 Agent Session #$InspectSessionId"
        $inspectPage = Invoke-ToDoGet "/AiSessions/Details?id=$InspectSessionId"
        Assert-NotLoginPage $inspectPage
        $inspectState = Get-AgentSessionPageState $inspectPage.Content
        $report.Agent = [ordered]@{
            AgentKey = $null
            SessionId = $InspectSessionId
            Status = $inspectState.SessionStatus
            RunJobId = $inspectState.RunJobId
            RunJobStatus = $inspectState.RunJobStatus
            Attempt = $inspectState.Attempt
            Steps = $inspectState.Steps
            JobText = $inspectState.JobText
            SessionError = $inspectState.SessionError
            DetailsUrl = (Resolve-ToDoUri "/AiSessions/Details?id=$InspectSessionId").AbsoluteUri
        }
        $report.Success = $true
        Write-Host ''
        Write-Host "Session #$InspectSessionId 诊断完成。" -ForegroundColor Green
        Write-Host "Session 状态：$($inspectState.SessionStatus)"
        Write-Host "运行任务：#$($inspectState.RunJobId) · $($inspectState.RunJobStatus) · 尝试 $($inspectState.Attempt) · 步骤 $($inspectState.Steps)"
        if (-not [string]::IsNullOrWhiteSpace($inspectState.JobText)) {
            Write-Host "运行信息：$($inspectState.JobText)"
        }
        if (-not [string]::IsNullOrWhiteSpace($inspectState.SessionError)) {
            Write-Host "Session 错误：$($inspectState.SessionError)" -ForegroundColor Red
        }
        return
    }

    $projectDescription = if ($DemoMode) {
        '星桥智造计划把现有人工巡店升级为智能巡检。项目覆盖华东20家门店，先在上海3家门店灰度，验证设备在线率、告警准确率、企业微信通知和门店处置闭环后再分批扩容。'
    }
    else {
        'Agent 自动化端到端验收项目。验证项目、任务、会议纪要、权限、队列和 DeepSeek Session。'
    }
    $projectRequirements = if ($DemoMode) {
        '目标上线日：2026-09-18；设备在线率≥99%；关键告警5分钟内触达；误报率≤3%；所有高风险变更必须经过人工审批；本项目全部人物、门店和业务数据均为演示模拟。'
    }
    else {
        '自动验收；测试数据；禁止用于正式业务'
    }

    Write-Step "创建$(if ($DemoMode) { '演示' } else { '测试' })项目 $projectName"
    $createProjectPage = Invoke-ToDoGet '/Projects/Create'
    Assert-NotLoginPage $createProjectPage
    $projectToken = Get-AntiForgeryToken $createProjectPage.Content
    $null = Invoke-ToDoFormPost '/Projects/Create' @{
        'Project.Name' = $projectName
        'Project.Description' = $projectDescription
        'Project.Requirements' = $projectRequirements
        'IsEncrypted' = '0'
    } $projectToken

    $encodedProjectName = [uri]::EscapeDataString($projectName)
    $filteredProjects = Invoke-ToDoGet "/Projects?Keyword=$encodedProjectName&PageSize=100"
    $projectId = Get-ProjectIdFromList $filteredProjects.Content $projectName
    $script:CreatedProjectId = $projectId
    $projectDetails = Invoke-ToDoGet "/Projects/Details/$projectId"
    Assert-ContainsText $projectDetails.Content $projectName '项目详情验收'
    $report.Project = [ordered]@{
        Id = $projectId
        Name = $projectName
        DetailsUrl = (Resolve-ToDoUri "/Projects/Details/$projectId").AbsoluteUri
    }

    $taskDefinitions = if ($DemoMode) { @(
        [ordered]@{
            Title = '门店清单与设备网络基线'
            Description = "## 当前进展`n已完成20家门店设备台账核验，3家灰度店完成弱网时段采样。`n`n- 负责人：林悦（实施经理）`n- 当前进度：80%`n- 验收：设备编号、固件版本、网络抖动记录齐全"
            Status = 'InProgress'
            Progress = 80
            Priority = 'High'
            DueDays = 2
        },
        [ordered]@{
            Title = '告警规则与阈值压测'
            Description = "## 当前进展`n离线、温度越界和客流设备异常三类告警已进入回放压测。`n`n- 负责人：周恺（平台工程师）`n- 当前进度：65%`n- 风险：弱网重连可能造成重复告警"
            Status = 'InProgress'
            Progress = 65
            Priority = 'High'
            DueDays = 5
        },
        [ordered]@{
            Title = '企业微信通知链路联调'
            Description = "## 当前进展`n已打通测试群机器人，待验证夜间升级和超时转派。`n`n- 负责人：顾宁（运营负责人）`n- 当前进度：30%`n- 验收：关键告警5分钟内到达且可追踪处置人"
            Status = 'InProgress'
            Progress = 30
            Priority = 'High'
            DueDays = 4
        },
        [ordered]@{
            Title = '3家灰度门店上线'
            Description = "## 上线范围`n上海静安店、徐汇店、浦东店。`n`n需完成门店培训、回滚演练和24小时值守安排后才能上线。"
            Status = 'NotStarted'
            Progress = 0
            Priority = 'High'
            DueDays = 8
        },
        [ordered]@{
            Title = '20家门店分批扩容与培训SOP'
            Description = "## 扩容原则`n灰度验收通过后按5家一批扩容；每批观察24小时。`n`n培训材料需覆盖告警确认、现场排查、升级和关闭流程。"
            Status = 'NotStarted'
            Progress = 0
            Priority = 'Medium'
            DueDays = 15
        },
        [ordered]@{
            Title = '设备接入协议与测试环境准备'
            Description = "## 完成说明`n设备接入协议、测试租户、演示群和回滚基线均已准备完成。"
            Status = 'Completed'
            Progress = 100
            Priority = 'Medium'
            DueDays = 0
        }
    ) }
    else { @(
        [ordered]@{
            Title = '[E2E] 需求范围确认'
            Description = "## 验收目标`n确认项目范围、验收边界和负责人。`n`n- 当前进度：60%`n- 风险：需求变更需要留痕"
            Status = 'InProgress'
            Progress = 60
            Priority = 'High'
            DueDays = 2
        },
        [ordered]@{
            Title = '[E2E] Agent权限与审批联调'
            Description = "## 验收目标`n验证低风险直接执行、中风险AI审核和高风险人工审批。"
            Status = 'InProgress'
            Progress = 30
            Priority = 'High'
            DueDays = 3
        },
        [ordered]@{
            Title = '[E2E] 项目资料分类回归'
            Description = "## 验收目标`n检查资料分类权限、读取审计和版本边界。"
            Status = 'NotStarted'
            Progress = 0
            Priority = 'Medium'
            DueDays = 5
        },
        [ordered]@{
            Title = '[E2E] 服务器发布检查'
            Description = "## 验收目标`n验证IIS站点、数据库迁移和登录访问。"
            Status = 'Completed'
            Progress = 100
            Priority = 'Medium'
            DueDays = 0
        }
    ) }

    foreach ($task in $taskDefinitions) {
        Write-Step "创建任务：$($task.Title)"
        $taskCreatePath = "/Tasks/Edit/$projectId"
        $taskPage = Invoke-ToDoGet $taskCreatePath
        Assert-NotLoginPage $taskPage
        $taskToken = Get-AntiForgeryToken $taskPage.Content
        $taskResponse = Invoke-ToDoFormPost $taskCreatePath @{
            'Task.Id' = '0'
            'Task.ConcurrencyVersion' = '1'
            'Task.ProjectId' = $projectId
            'ProjectId' = $projectId
            'Task.Title' = $task.Title
            'Task.Description' = $task.Description
            'Task.AssigneeType' = 'Human'
            'Task.Status' = $task.Status
            'Task.Progress' = $task.Progress
            'Task.EndTime' = (Get-Date).Date.AddDays($task.DueDays).AddHours(18).ToString('yyyy-MM-ddTHH:mm')
            'Task.Priority' = $task.Priority
            'LabelNames' = if ($DemoMode) { '智能巡检,华东门店,演示项目' } else { 'E2E,自动验收' }
            'ReturnPage' = 'Index'
        } $taskToken
        $taskResponseUri = Get-ResponseUri $taskResponse
        if ($taskResponseUri -match "/Tasks/Edit/$projectId") {
            throw "任务表单验证失败：$($task.Title)"
        }

        $encodedTaskTitle = [uri]::EscapeDataString($task.Title)
        $taskList = Invoke-ToDoGet "/Tasks?projectId=$projectId&keyword=$encodedTaskTitle"
        $taskId = Get-TaskIdFromList $taskList.Content $task.Title
        $report.Tasks += [ordered]@{
            Id = $taskId
            Title = $task.Title
            Status = $task.Status
            DetailsUrl = (Resolve-ToDoUri "/Tasks/Details/$taskId").AbsoluteUri
        }
        $taskDetails = Invoke-ToDoGet "/Tasks/Details/$taskId"
        Assert-NotLoginPage $taskDetails
        Assert-ContainsText $taskDetails.Content $task.Title '任务详情验收'
    }

    $documentEvidence = $null
    if ($DemoMode) {
        $documentUploadName = 'Xingqiao-Intelligent-Inspection-Pilot-V1.0.docx'
        Write-Step "上传并索引项目资料：$documentUploadName"
        $materialsPage = Invoke-ToDoGet "/Projects/Materials?projectId=$projectId"
        Assert-NotLoginPage $materialsPage
        $materialsToken = Get-AntiForgeryToken $materialsPage.Content
        $uploadResponse = Invoke-ToDoMultipartPost "/Projects/Materials?handler=Upload" @{
            'ProjectId' = $projectId
            'Description' = 'V1.0 评审版：试点范围、里程碑、验收标准、风险和角色分工'
            'UploadCategory' = 'RequirementsAndGoals'
            'UploadCategoryId' = ''
            'UploadCustomCategory' = ''
        } $materialsToken $DemoDocumentPath 'UploadFile' $documentUploadName
        Assert-ContainsText $uploadResponse.Content '资料已上传为 v' '项目资料上传验收'

        Write-Step "授权 $AgentKey 只读访问“需求与目标”资料"
        $permissionToken = Get-AntiForgeryToken $uploadResponse.Content
        $permissionResponse = Invoke-ToDoFormPost '/Projects/Materials?handler=SavePermission' @{
            'ProjectId' = $projectId
            'PermissionAgentKey' = $AgentKey
            'PermissionCategory' = 'RequirementsAndGoals'
            'PermissionCustomCategoryId' = ''
            'PermissionCanRead' = 'true'
            'PermissionCanWrite' = 'false'
        } $permissionToken
        Assert-ContainsText $permissionResponse.Content 'Agent 资料权限已更新' 'Agent资料权限验收'

        $indexDeadline = (Get-Date).AddSeconds(120)
        do {
            $documentIndexStatus = Get-DocumentIndexStatus $permissionResponse.Content $documentUploadName
            Write-Step "项目资料索引状态：$documentIndexStatus"
            if ($documentIndexStatus -eq '可检索') {
                break
            }
            if ($documentIndexStatus -in @('索引失败', '暂不支持', '未找到')) {
                throw "项目资料无法用于 Agent 检索：$documentIndexStatus"
            }
            if ((Get-Date) -ge $indexDeadline) {
                throw "等待项目资料索引超时：$documentUploadName"
            }
            Start-Sleep -Seconds 2
            $permissionResponse = Invoke-ToDoGet "/Projects/Materials?projectId=$projectId"
        } while ($true)

        $documentEvidence = $documentUploadName
        $report.Documents += [ordered]@{
            FileName = $documentUploadName
            Category = '需求与目标'
            IndexStatus = $documentIndexStatus
            AgentPermission = "$AgentKey：只读"
            MaterialsUrl = (Resolve-ToDoUri "/Projects/Materials?projectId=$projectId").AbsoluteUri
        }
    }

    $meetingDefinitions = if ($DemoMode) { @(
        [ordered]@{
            Title = '上线门禁评审会'
            Date = (Get-Date).Date.AddDays(-2)
            Content = @"
# 开会原因
确认智能巡检从测试环境进入3家门店灰度前是否满足上线门禁。

# 讨论议题
1. 设备在线率和弱网重连是否达到灰度标准。
2. 告警误报、通知时效和夜间升级机制是否可控。
3. 回滚、值守和问题升级责任是否明确。

# 项目进展
- 门店清单与设备网络基线完成 80%。
- 告警规则与阈值压测完成 65%。
- 企业微信通知链路联调完成 30%。

# 会议决策
- 上海静安店、徐汇店、浦东店作为首批灰度门店。
- 关键告警触达超过5分钟即视为阻断项，不得扩容。
- 任何门店批量扩容或任务截止时间变更都必须经项目负责人确认。

# 行动项
- 周恺在五天内完成告警回放压测并提交误报分析。
- 顾宁在四天内完成企业微信夜间升级链路验证。
- 林悦补齐三家灰度店的回滚联系人和现场值守表。
"@
            Transcript = '林悦：三家灰度店的设备台账已经核对完，弱网采样还差晚高峰一轮。周恺：离线告警回放目前误报率是百分之四点二，必须降到百分之三以内。顾宁：夜间告警先到值班群，十分钟没人确认就升级给区域经理。洪恩泽：误报率和五分钟触达是上线门禁，不满足就不扩容。'
        },
        [ordered]@{
            Title = '弱网与告警误报风险复盘会'
            Date = (Get-Date).Date.AddDays(-1)
            Content = @"
# 开会原因
复盘门店弱网环境下出现的重复告警，决定修复顺序和验收方法。

# 讨论议题
1. 重连抖动为什么产生重复离线告警。
2. 去重窗口设置是否会漏掉真实故障。
3. 灰度上线期间如何观测和快速回滚。

# 会议结论
- 采用设备编号加告警类型作为去重键，窗口暂定90秒。
- 去重规则先在测试租户回放24小时，再进入三家灰度店。
- 灰度阶段每天17:30输出一次在线率、误报率和平均触达时长。

# 行动项
- 周恺今天完成90秒去重窗口的回放数据。
- 林悦补充弱网门店名单，并标出4G备链可用情况。
- 顾宁准备门店值班人员的一页式处置卡。
"@
            Transcript = '周恺：重复告警集中在网络恢复后的三十秒内，同一设备会连续上报两到三次。林悦：浦东店晚高峰丢包明显，现场有4G备链但还没纳入演练。顾宁：去重不能影响真实离线升级，我建议灰度日报把原始告警数和去重后告警数都列出来。洪恩泽：先按九十秒做回放，数据通过后再上线。'
        }
    ) }
    else { @(
        [ordered]@{
            Title = '[E2E] 上次迭代复盘会议'
            Date = (Get-Date).Date.AddDays(-1)
            Content = @"
# 开会原因
核对服务器发布后的实际状态，确认 Agent 自动化进入可验收阶段。

# 讨论议题
1. 登录、项目和任务主链路是否正常。
2. Agent 权限和审批是否存在越权风险。
3. 本次迭代未完成事项如何安排。

# 项目进展
- 服务器发布检查已经完成。
- 需求范围确认进度为 60%。
- Agent 权限与审批联调进度为 30%。

# 会议决策
- 低风险任务评论允许直接写入。
- 中风险新增操作交给 AI 自动审核，不确定时转人工。
- 修改任务、项目和资料等高风险操作必须人工审批。

# 行动项
- 完成 Agent 权限与审批联调，截止三天内。
- 完成项目资料分类回归，截止五天内。
"@
            Transcript = '洪恩泽：先验证服务器版本。成员A：任务主链路正常。成员B：建议保留高风险人工审批。洪恩泽：同意，并安排后续回归。'
        },
        [ordered]@{
            Title = '[E2E] 本次迭代计划会议'
            Date = (Get-Date).Date
            Content = @"
# 开会原因
在继续开发前同步项目状态、未完成任务、风险和验收安排。

# 会议议题
1. 查看全部未完成任务。
2. 确认未来三天到期任务。
3. 验收 Agent Session、自动任务和事件触发。

# 会议决策
- 先完成 Agent 权限与审批联调。
- 自动化验收必须保留 Session、工具调用和审批记录。
- 测试项目完成后归档，不与正式项目混用。

# 行动项
- 今天执行一次项目工作台 Agent 只读汇总。
- 验收失败时记录错误地址、Session 和运行任务编号。
"@
            Transcript = '主持人：今天重点验收Agent。洪恩泽：先创建测试项目和任务。成员A：会议纪要要包含原因、议题、决策和行动项。'
        }
    ) }

    foreach ($meeting in $meetingDefinitions) {
        Write-Step "创建会议纪要：$($meeting.Title)"
        $meetingPage = Invoke-ToDoGet "/MeetingMinutes/Create?ProjectId=$projectId"
        Assert-NotLoginPage $meetingPage
        $meetingToken = Get-AntiForgeryToken $meetingPage.Content
        $meetingResponse = Invoke-ToDoFormPost "/MeetingMinutes/Create?ProjectId=$projectId" @{
            'MeetingMinutes.ProjectId' = $projectId
            'MeetingMinutes.MeetingTitle' = $meeting.Title
            'MeetingMinutes.MeetingDate' = $meeting.Date.ToString('yyyy-MM-dd')
            'MeetingMinutes.MeetingContent' = $meeting.Content.Trim()
            'MeetingMinutes.TranscriptText' = $meeting.Transcript
            'MeetingMinutes.IsDraft' = 'false'
            'TencentSourceFlag' = '0'
            'RawTranscriptJson' = ''
            'CleanContent' = ''
            'SelectedRecordIds' = ''
        } $meetingToken
        $meetingUri = Get-ResponseUri $meetingResponse
        $meetingId = Get-IdFromDetailsUri $meetingUri '会议纪要'
        $report.Meetings += [ordered]@{
            Id = $meetingId
            Title = $meeting.Title
            Date = $meeting.Date.ToString('yyyy-MM-dd')
            DetailsUrl = (Resolve-ToDoUri "/MeetingMinutes/Details?id=$meetingId").AbsoluteUri
        }
        Assert-ContainsText $meetingResponse.Content $meeting.Title '会议纪要详情验收'
        Assert-ContainsText $meetingResponse.Content '会议决策' '会议纪要结构验收'
    }

    if (-not $SkipAgent) {
        Write-Step "创建数字员工任务并验证调度 Agent 自动派单"
        $agentsPage = Invoke-ToDoGet '/Agents'
        Assert-NotLoginPage $agentsPage
        Assert-ContainsText $agentsPage.Content $AgentKey 'Agent 注册验收'
        $taskEvidence = if ($DemoMode) { $taskDefinitions[1].Title } else { $taskDefinitions[0].Title }
        $meetingEvidence = $meetingDefinitions[0].Title
        $agentTaskTitle = if ($DemoMode) {
            "输出灰度上线7天行动简报（Agent自动调度）-$randomSuffix"
        }
        else {
            "[E2E] Agent 自动调度项目工作台汇总 $randomSuffix"
        }
        $agentTaskDescription = if ($DemoMode) { @"
你是项目经理的数字员工。请只读取当前项目，不修改任何数据，输出一份可直接用于晨会的“未来7天灰度上线行动简报”。

简报必须包含：
1. 当前总体判断与是否具备灰度条件；
2. 按优先级排列的未完成任务、负责人、截止时间和阻塞项；
3. 最近会议已经确认的决策和带责任人的行动项；
4. 从项目方案 Word 资料中提取的上线门禁或验收指标；
5. 三条下一步建议，并明确哪些动作仍需人审批。

为了演示证据链，请在回答中原样引用以下四个标识，并说明分别来自项目、任务、会议和项目资料：
- $projectName
- $taskEvidence
- $meetingEvidence
- $documentEvidence

不要虚构数据；信息不足时明确写“待确认”。
"@ }
        else { @"
请只读取当前项目，不修改任何数据。汇总项目进展、未完成任务、最近会议决策和下一步行动。
为了自动验收，请在回答中原样包含以下三个标识：
1. $projectName
2. $taskEvidence
3. $meetingEvidence
"@ }
        $agentTaskCreatePath = "/Tasks/Edit/$projectId"
        $agentTaskCreatePage = Invoke-ToDoGet $agentTaskCreatePath
        Assert-NotLoginPage $agentTaskCreatePage
        $agentTaskToken = Get-AntiForgeryToken $agentTaskCreatePage.Content
        $agentTaskResponse = Invoke-ToDoFormPost $agentTaskCreatePath @{
            'Task.Id' = '0'
            'Task.ConcurrencyVersion' = '1'
            'Task.ProjectId' = $projectId
            'ProjectId' = $projectId
            'Task.Title' = $agentTaskTitle
            'Task.Description' = $agentTaskDescription.Trim()
            'Task.AssigneeType' = 'DigitalEmployee'
            'Task.Status' = 'NotStarted'
            'Task.Progress' = '0'
            'Task.EndTime' = (Get-Date).Date.AddDays(1).AddHours(18).ToString('yyyy-MM-ddTHH:mm')
            'Task.Priority' = 'Medium'
            'LabelNames' = if ($DemoMode) { 'Agent自动调度,晨会简报,只读任务' } else { 'E2E,Agent自动调度,项目汇总' }
            'ReturnPage' = 'Index'
        } $agentTaskToken
        $agentTaskResponseUri = Get-ResponseUri $agentTaskResponse
        if ($agentTaskResponseUri -match "/Tasks/Edit/$projectId") {
            throw "数字员工任务表单验证失败：$agentTaskTitle"
        }

        $encodedAgentTaskTitle = [uri]::EscapeDataString($agentTaskTitle)
        $agentTaskList = Invoke-ToDoGet "/Tasks?projectId=$projectId&keyword=$encodedAgentTaskTitle"
        $agentTaskId = Get-TaskIdFromList $agentTaskList.Content $agentTaskTitle
        $report.Tasks += [ordered]@{
            Id = $agentTaskId
            Title = $agentTaskTitle
            Status = 'NotStarted'
            AssigneeType = 'DigitalEmployee'
            DetailsUrl = (Resolve-ToDoUri "/Tasks/Details/$agentTaskId").AbsoluteUri
        }

        $agentTaskPage = Invoke-ToDoGet "/Tasks/Details/$agentTaskId"
        Assert-NotLoginPage $agentTaskPage
        Assert-ContainsText $agentTaskPage.Content $agentTaskTitle '数字员工任务详情验收'
        $decodedAgentTaskPage = [System.Net.WebUtility]::HtmlDecode($agentTaskPage.Content)
        if ($decodedAgentTaskPage.Contains('没有可接单的 Agent')) {
            throw "调度 Agent 没有找到可接单候选：$(Get-ResponseUri $agentTaskPage)"
        }
        if ($decodedAgentTaskPage.Contains('调度置信度不足，需要确认')) {
            $recommendedAgentId = Get-RecommendedDispatchAgentId $agentTaskPage.Content
            Write-Step "调度置信度不足，确认推荐候选 Agent #$recommendedAgentId"
            $dispatchToken = Get-AntiForgeryToken $agentTaskPage.Content
            $agentTaskPage = Invoke-ToDoFormPost "/Tasks/Details/${agentTaskId}?handler=ConfirmDispatch" @{
                'SelectedDispatchAgentId' = $recommendedAgentId
            } $dispatchToken
            Assert-ContainsText $agentTaskPage.Content 'Agent 派单已确认' '调度确认验收'
        }

        $deadline = (Get-Date).AddSeconds($AgentTimeoutSeconds)
        $lastStateSignature = ''
        $report.Agent = [ordered]@{
            AgentKey = $AgentKey
            TaskId = $agentTaskId
            SessionId = $null
            Status = 'Agent待执行'
            WorkItemId = $null
            WorkItemStatus = '未创建'
            Attempt = ''
            Steps = ''
            Error = ''
            ProjectEvidenceFound = $false
            TaskEvidenceFound = $false
            MeetingEvidenceFound = $false
            DocumentEvidenceFound = if ($DemoMode) { $false } else { $null }
            ReadOnlyBoundaryVerified = if ($DemoMode) { $false } else { $null }
            TaskDetailsUrl = (Resolve-ToDoUri "/Tasks/Details/$agentTaskId").AbsoluteUri
            SessionDetailsUrl = $null
        }

        do {
            $taskState = Get-AgentTaskPageState $agentTaskPage.Content
            if ($taskState.AgentStatus -eq '等待确认执行计划' -and $ConfirmAgentPlans) {
                $agentTaskPage = Confirm-DemoExecutionPlan $agentTaskId $agentTaskPage
                $taskState = Get-AgentTaskPageState $agentTaskPage.Content
            }
            $report.Agent.Status = $taskState.AgentStatus
            $report.Agent.WorkItemId = $taskState.WorkItemId
            $report.Agent.WorkItemStatus = $taskState.WorkItemStatus
            $report.Agent.Attempt = $taskState.Attempt
            $report.Agent.Steps = $taskState.Steps
            $report.Agent.Error = $taskState.Error
            if ($taskState.SessionId) {
                $report.Agent.SessionId = $taskState.SessionId
                $report.Agent.SessionDetailsUrl = (Resolve-ToDoUri "/AiSessions/Details?id=$($taskState.SessionId)").AbsoluteUri
            }
            $stateSignature = "$($taskState.AgentStatus)|$($taskState.WorkItemStatus)|$($taskState.Attempt)|$($taskState.Steps)|$($taskState.SessionId)|$($taskState.Error)"
            if ($stateSignature -ne $lastStateSignature) {
                Write-Step "任务 #$agentTaskId：$($taskState.AgentStatus)；工作项 #$($taskState.WorkItemId) $($taskState.WorkItemStatus)；尝试 $($taskState.Attempt)；步骤 $($taskState.Steps)"
                if ($taskState.AgentStatus -in @('等待确认执行计划', '等待确认 Agent 派单')) {
                    Write-Step "需要人工确认，请打开：$($report.Agent.TaskDetailsUrl)"
                }
                $lastStateSignature = $stateSignature
            }
            if ($taskState.AgentStatus -eq '执行失败' -or $taskState.WorkItemStatus -eq '失败') {
                throw "Agent 任务 #$agentTaskId 执行失败：$($taskState.Error)"
            }
            if ($taskState.AgentStatus -eq '等待自动重试' -or $taskState.WorkItemStatus -eq '等待重试') {
                throw "Agent 任务 #$agentTaskId 已进入等待重试：$($taskState.Error)"
            }
            if ($taskState.AgentStatus -eq '等待敏感操作审批' -or $taskState.WorkItemStatus -eq '等待审批') {
                throw "只读 Agent 任务 #$agentTaskId 意外进入人工审批"
            }
            if ($taskState.WorkItemStatus -eq '已完成' -and $taskState.AgentStatus -in @('待人工确认', '已确认完成')) {
                break
            }
            if ((Get-Date) -ge $deadline) {
                throw "等待 Agent 任务 #$agentTaskId 超时（$AgentTimeoutSeconds 秒）：$($report.Agent.TaskDetailsUrl)"
            }
            Start-Sleep -Seconds 3
            $agentTaskPage = Invoke-ToDoGet "/Tasks/Details/$agentTaskId"
        } while ($true)

        if (-not $report.Agent.SessionId) {
            throw "Agent 工作项已完成，但任务 #$agentTaskId 没有关联 Session。"
        }
        if (-not $DemoMode -and $report.Agent.Steps -notmatch '^1/') {
            throw "只读 Agent 任务 #$agentTaskId 应在一轮内完成，实际步骤为 $($report.Agent.Steps)。"
        }
        if ($DemoMode -and $report.Agent.Steps -notmatch '^[1-5]/5$') {
            throw "演示 Agent 任务 #$agentTaskId 的步骤计数异常：$($report.Agent.Steps)。"
        }
        if (-not $DemoMode) {
            Assert-ContainsText $agentTaskPage.Content '暂无评论' 'Agent 只读任务无写入验收'
        }
        $sessionId = $report.Agent.SessionId
        $sessionPage = Invoke-ToDoGet "/AiSessions/Details?id=$sessionId"
        if ($DemoMode -and [regex]::IsMatch(
                $sessionPage.Content,
                '(?is)<div class="fw-semibold">\s*task\.add_comment\s*</div>')) {
            throw "只读 Agent 任务 #$agentTaskId 调用了 task.add_comment，后端只读边界未生效。"
        }
        Assert-ContainsText $sessionPage.Content $AgentKey 'Agent 自动调度结果验收'
        Assert-ContainsText $sessionPage.Content $projectName 'Agent 项目上下文验收'
        Assert-ContainsText $sessionPage.Content $taskEvidence 'Agent 任务上下文验收'
        Assert-ContainsText $sessionPage.Content $meetingEvidence 'Agent 会议上下文验收'
        if ($DemoMode) {
            Assert-ContainsText $sessionPage.Content $documentEvidence 'Agent 项目资料上下文验收'
            $report.Agent.DocumentEvidenceFound = $true
            $report.Agent.ReadOnlyBoundaryVerified = $true
        }
        $report.Agent.ProjectEvidenceFound = $true
        $report.Agent.TaskEvidenceFound = $true
        $report.Agent.MeetingEvidenceFound = $true

        if ($DemoMode) {
            Write-Step '创建高风险变更任务并验证人工审批门禁'
            $approvalTaskTitle = "调整本任务交付截止时间（人工审批演示）-$randomSuffix"
            $approvalDeadline = (Get-Date).Date.AddDays(4).ToString('yyyy-MM-dd')
            $approvalTaskDescription = @"
请把当前任务自身的截止日期调整为 $approvalDeadline。
必须调用 task.update 工具提交这项变更，不得绕过人工审批，也不得只在文字中声称已经修改。提交审批后停止并等待审批结果；不要添加任务评论。
"@
            $approvalTaskCreatePath = "/Tasks/Edit/$projectId"
            $approvalTaskCreatePage = Invoke-ToDoGet $approvalTaskCreatePath
            $approvalTaskToken = Get-AntiForgeryToken $approvalTaskCreatePage.Content
            $approvalTaskResponse = Invoke-ToDoFormPost $approvalTaskCreatePath @{
                'Task.Id' = '0'
                'Task.ConcurrencyVersion' = '1'
                'Task.ProjectId' = $projectId
                'ProjectId' = $projectId
                'Task.Title' = $approvalTaskTitle
                'Task.Description' = $approvalTaskDescription.Trim()
                'Task.AssigneeType' = 'DigitalEmployee'
                'Task.Status' = 'NotStarted'
                'Task.Progress' = '0'
                'Task.EndTime' = (Get-Date).Date.AddDays(2).AddHours(18).ToString('yyyy-MM-ddTHH:mm')
                'Task.Priority' = 'High'
                'LabelNames' = 'Agent自动调度,高风险变更,人工审批'
                'ReturnPage' = 'Index'
            } $approvalTaskToken
            if ((Get-ResponseUri $approvalTaskResponse) -match "/Tasks/Edit/$projectId") {
                throw "高风险审批演示任务表单验证失败：$approvalTaskTitle"
            }

            $encodedApprovalTaskTitle = [uri]::EscapeDataString($approvalTaskTitle)
            $approvalTaskList = Invoke-ToDoGet "/Tasks?projectId=$projectId&keyword=$encodedApprovalTaskTitle"
            $approvalTaskId = Get-TaskIdFromList $approvalTaskList.Content $approvalTaskTitle
            $report.Tasks += [ordered]@{
                Id = $approvalTaskId
                Title = $approvalTaskTitle
                Status = 'NotStarted'
                AssigneeType = 'DigitalEmployee'
                DetailsUrl = (Resolve-ToDoUri "/Tasks/Details/$approvalTaskId").AbsoluteUri
            }

            $approvalTaskPage = Invoke-ToDoGet "/Tasks/Details/$approvalTaskId"
            $decodedApprovalTaskPage = [System.Net.WebUtility]::HtmlDecode($approvalTaskPage.Content)
            if ($decodedApprovalTaskPage.Contains('没有可接单的 Agent')) {
                throw "高风险审批演示任务没有找到可接单候选：$approvalTaskId"
            }
            if ($decodedApprovalTaskPage.Contains('调度置信度不足，需要确认')) {
                $recommendedApprovalAgentId = Get-RecommendedDispatchAgentId $approvalTaskPage.Content
                Write-Step "高风险任务调度置信度不足，确认推荐候选 Agent #$recommendedApprovalAgentId"
                $approvalDispatchToken = Get-AntiForgeryToken $approvalTaskPage.Content
                $approvalTaskPage = Invoke-ToDoFormPost "/Tasks/Details/${approvalTaskId}?handler=ConfirmDispatch" @{
                    'SelectedDispatchAgentId' = $recommendedApprovalAgentId
                } $approvalDispatchToken
            }

            $approvalDeadlineAt = (Get-Date).AddSeconds($AgentTimeoutSeconds)
            $approvalLastState = ''
            do {
                $approvalState = Get-AgentTaskPageState $approvalTaskPage.Content
                if ($approvalState.AgentStatus -eq '等待确认执行计划' -and $ConfirmAgentPlans) {
                    $approvalTaskPage = Confirm-DemoExecutionPlan $approvalTaskId $approvalTaskPage
                    $approvalState = Get-AgentTaskPageState $approvalTaskPage.Content
                }
                $approvalSignature = "$($approvalState.AgentStatus)|$($approvalState.WorkItemStatus)|$($approvalState.Attempt)|$($approvalState.Steps)|$($approvalState.SessionId)|$($approvalState.Error)"
                if ($approvalSignature -ne $approvalLastState) {
                    Write-Step "审批演示任务 #$approvalTaskId：$($approvalState.AgentStatus)；工作项 #$($approvalState.WorkItemId) $($approvalState.WorkItemStatus)；步骤 $($approvalState.Steps)"
                    if ($approvalState.AgentStatus -in @('等待确认执行计划', '等待确认 Agent 派单')) {
                        Write-Step "需要人工确认，请打开：$((Resolve-ToDoUri "/Tasks/Details/$approvalTaskId").AbsoluteUri)"
                    }
                    $approvalLastState = $approvalSignature
                }
                if ($approvalState.AgentStatus -eq '等待敏感操作审批' -and $approvalState.WorkItemStatus -eq '等待审批') {
                    break
                }
                if ($approvalState.AgentStatus -eq '执行失败' -or $approvalState.WorkItemStatus -eq '失败') {
                    throw "审批演示任务 #$approvalTaskId 执行失败：$($approvalState.Error)"
                }
                if ($approvalState.AgentStatus -eq '待人工确认' -or $approvalState.WorkItemStatus -eq '已完成') {
                    throw "审批演示任务 #$approvalTaskId 未经过人工审批就完成。"
                }
                if ((Get-Date) -ge $approvalDeadlineAt) {
                    throw "等待审批演示任务 #$approvalTaskId 进入人工审批超时。"
                }
                Start-Sleep -Seconds 3
                $approvalTaskPage = Invoke-ToDoGet "/Tasks/Details/$approvalTaskId"
            } while ($true)

            if (-not $approvalState.SessionId) {
                throw "审批演示任务 #$approvalTaskId 没有关联 Session。"
            }
            $approvalSessionPage = Invoke-ToDoGet "/AiSessions/Details?id=$($approvalState.SessionId)"
            if (-not [regex]::IsMatch(
                    $approvalSessionPage.Content,
                    '(?is)<div class="fw-semibold">\s*task\.update\s*</div>')) {
                throw "审批演示 Session #$($approvalState.SessionId) 中没有 task.update 工具调用。"
            }

            $report.ApprovalDemo = [ordered]@{
                TaskId = $approvalTaskId
                TaskTitle = $approvalTaskTitle
                AgentStatus = $approvalState.AgentStatus
                WorkItemId = $approvalState.WorkItemId
                WorkItemStatus = $approvalState.WorkItemStatus
                SessionId = $approvalState.SessionId
                Tool = 'task.update'
                ExpectedDeadline = $approvalDeadline
                ApprovalCenterUrl = (Resolve-ToDoUri '/Approvals').AbsoluteUri
                TaskDetailsUrl = (Resolve-ToDoUri "/Tasks/Details/$approvalTaskId").AbsoluteUri
                SessionDetailsUrl = (Resolve-ToDoUri "/AiSessions/Details?id=$($approvalState.SessionId)").AbsoluteUri
            }
        }
    }
    else {
        $report.Agent = [ordered]@{
            AgentKey = $AgentKey
            Status = 'Skipped'
        }
    }

    if ($ArchiveAfterRun) {
        Archive-TestProject
    }

    $report.Success = $true
    Write-Host ''
    Write-Host "$(if ($DemoMode) { '真实演示项目创建并验收通过。' } else { '端到端验收通过。' })" -ForegroundColor Green
    Write-Host "项目：$projectName (#$projectId)"
    Write-Host "任务：$($report.Tasks.Count) 个；会议纪要：$($report.Meetings.Count) 个；项目资料：$($report.Documents.Count) 个"
    if (-not $SkipAgent) {
        Write-Host "Agent 任务：#$($report.Agent.TaskId)；Session：#$($report.Agent.SessionId)"
        if ($DemoMode -and $null -ne $report.ApprovalDemo) {
            Write-Host "审批演示：任务 #$($report.ApprovalDemo.TaskId) 正在等待人工审批"
        }
    }
}
catch {
    $report.Error = $_.Exception.Message
    Write-Host ''
    Write-Host "端到端验收失败：$($report.Error)" -ForegroundColor Red
    if ($ArchiveAfterRun -and $script:CreatedProjectId) {
        try {
            Archive-TestProject
        }
        catch {
            Write-Warning "测试失败后归档项目也失败：$($_.Exception.Message)"
        }
    }
    throw
}
finally {
    $report.ArchivedAfterRun = $script:ProjectArchived
    Save-Report
    $ProgressPreference = $previousProgressPreference
    Write-Host "验收报告：$OutputPath"
}
