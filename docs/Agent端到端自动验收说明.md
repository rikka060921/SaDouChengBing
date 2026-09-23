# Agent 端到端自动验收说明

脚本位置：`deploy/Test-ToDo-AgentE2E.ps1`

该脚本通过网站真实 HTTP 页面完成验收，不直接连接或修改 MySQL。它会依次测试：

1. 登录和 Cookie 会话；
2. Razor 防伪令牌；
3. 创建一个带唯一 `E2E-Agent-*` 名称的测试项目；
4. 创建 4 个包含不同状态、进度、优先级和截止日期的任务；
5. 创建 2 份包含开会原因、议题、项目进展、会议决策、行动项和原始记录的会议纪要；
6. 创建一个不指定具体数字员工的 Agent 任务，由调度 Agent 按能力、项目权限、风险、负载和历史表现选择执行 Agent；
7. 调度置信度不足时自动确认推荐候选，并等待后台工作项执行完成；
8. 验证任务状态进入“待人工确认”，并确认 Agent 回答确实读取到了项目、任务和会议上下文；
9. 输出不含密码和 Key 的 JSON 验收报告；
10. 可选地在测试结束后归档测试项目。

## 运行要求

- 目标网站已经发布并可通过 HTTPS 访问；
- 数据库迁移已经应用；
- 验收账号可以创建项目、任务和会议纪要，并有权确认自己项目中的 Agent 派单；
- 运行 Agent 时，服务器已经配置有效的 `AI:ApiKey`、`AI:ApiUrl` 和 `AI:ModelName`；
- `project-workbench` 已启用并允许接受调度任务；
- Windows PowerShell 5.1 或 PowerShell 7。

## 推荐运行方式

以管理员或普通 PowerShell 打开服务器源码目录：

```powershell
Set-Location "C:\Deploy\ToDo-source"
Set-ExecutionPolicy -Scope Process Bypass

$credential = Get-Credential

& ".\deploy\Test-ToDo-AgentE2E.ps1" `
    -BaseUrl "https://test1.icode8.net" `
    -Credential $credential
```

`Get-Credential` 会弹出安全凭据输入框。账号密码不会写入命令历史、脚本、仓库或验收报告。

验收完成后，控制台会显示：

- 测试项目 ID；
- 创建的任务和会议数量；
- 数字员工任务 ID、Agent 工作项状态和 Session ID；
- JSON 报告路径。

默认报告写在：

```text
%TEMP%\ToDo-Agent-E2E\
```

## 测试后自动归档

如果不希望测试项目继续显示在活跃项目列表中：

```powershell
$credential = Get-Credential

& ".\deploy\Test-ToDo-AgentE2E.ps1" `
    -BaseUrl "https://test1.icode8.net" `
    -Credential $credential `
    -ArchiveAfterRun
```

归档不是删除。任务、会议、Session 和审计记录仍然保留，便于复盘验收结果。

## 只测试项目、任务和会议

如果服务器暂时没有配置 DeepSeek，或者不希望本次验收产生模型费用：

```powershell
$credential = Get-Credential

& ".\deploy\Test-ToDo-AgentE2E.ps1" `
    -BaseUrl "https://test1.icode8.net" `
    -Credential $credential `
    -SkipAgent
```

会议纪要保存流程本身仍可能调用行动项解析；解析失败不会回滚已经成功保存的会议纪要。

## 指定预期 Agent 和超时

`AgentKey` 是调度结果的验收预期，不会绕过调度器直接指定 Agent。脚本仍由调度 Agent 自动评分和推荐；如果最终选择的 Agent 与预期不符，验收会失败并保留任务、工作项和 Session 便于排查。

```powershell
& ".\deploy\Test-ToDo-AgentE2E.ps1" `
    -BaseUrl "https://test1.icode8.net" `
    -Credential $credential `
    -AgentKey "project-workbench" `
    -AgentTimeoutSeconds 360 `
    -RequestTimeoutSeconds 240
```

## 诊断已经存在的 Session

诊断模式不会创建项目、任务、会议或新的 Agent Session，只读取指定 Session 当前状态：

```powershell
$credential = Get-Credential

& ".\deploy\Test-ToDo-AgentE2E.ps1" `
    -BaseUrl "https://test1.icode8.net" `
    -Credential $credential `
    -InspectSessionId 36
```

输出包括 Session 状态、后台运行任务编号、运行任务状态、尝试次数、步骤数和页面中记录的错误。

脚本遇到以下任一情况会返回非零退出结果：

- 登录失败或会话失效；
- 页面没有防伪令牌；
- 项目、任务或会议表单验证失败；
- 创建后无法打开详情；
- 预期 Agent 不存在、已停用或调度结果与预期不一致；
- 调度器没有候选，或低置信度推荐无法确认；
- Agent 任务进入失败、等待重试或意外审批；
- Agent 工作项完成后，任务没有进入“待人工确认”；
- Agent 回答没有包含测试项目、任务或会议标识；
- 等待 Agent 超时。

## 数据和安全约定

- 脚本不包含任何账号密码、DeepSeek Key、数据库密码或服务器凭据；
- 每次运行生成唯一项目，不会覆盖以前的测试；
- 所有测试任务和会议标题带 `[E2E]` 标记；
- 不要把真实生产账号密码直接写在命令行参数中；
- 若失败发生在项目创建以后，控制台和 JSON 报告会保留已创建项目 ID，便于手工检查；
- 正式生产环境运行前，建议先做好数据库备份并使用专门的验收账号。
