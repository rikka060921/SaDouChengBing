# 撒豆成兵

面向团队协作的智能项目工作台。系统将项目、任务、会议纪要、日报、资料知识库和数字员工（Agent）放在同一个权限边界内，让用户只需描述任务，由调度器选择合适的 Agent 执行、调用工具并回写结果。

## 当前能力

- 项目、成员、任务组、任务、会议纪要、日报、通知和操作日志。
- Agent 自动调度：按用途、实际运行能力、项目资料权限和所需工具筛选；多名合格候选按待处理量、注册顺序选择，不计算综合分或置信度。
- Agent 后台执行：支持持续会话、工具调用、人工审批、失败重试、取消和状态回写。
- 统一待办：集中处理无法匹配、明确要求的确认、敏感操作审批、无法自动核验的交付和执行失败；普通成功不重复打扰成员。
- Agent 日常管理：三字段创建、简化工作要求编辑、启用/停用和能力说明；模型、工具、历史版本与旧发布流程保留在技术维护入口。
- Agent 执行：草稿、分析、拆分建议默认只输出成果；明确保存或创建业务记录才开放对应工具。普通只读任务直接执行，必要时规划。
- 项目知识库：安全上传与下载，解析 PDF、Word、PowerPoint、Excel 和文本，后台分块索引并按权限检索。
- 会议行动督办：行动项关联任务、到期提醒、阻塞升级、人工确认和结构化验收建议。
- 自动化：领域事件、定时任务、个人每日摘要，具备数据库抢占锁、故障恢复和退避策略。
- 外部集成：企业微信应用消息、腾讯会议创建及转写接入，调用审计默认脱敏。
- 上线安全：全站默认鉴权、项目级权限、登录锁定与限流、安全 Cookie/CSP、审批与交付 HMAC 防篡改签名、生产配置校验和健康检查。
- 运维治理：标准 Activity/Meter 遥测、系统健康页、数据留存预览、分批归档和执行审计。

## 技术栈

- ASP.NET Core 8 Razor Pages
- Entity Framework Core 8 + MySQL 8
- ASP.NET Core Identity
- xUnit

## 本地启动

不要把密码或 API Key 写入仓库。使用 User Secrets：

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "server=127.0.0.1;port=3306;database=todo123;user=todo_app;password=你的密码;CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True" --project .\ToDo.Razor
dotnet user-secrets set "AI:ApiKey" "你的模型 Key" --project .\ToDo.Razor
dotnet user-secrets set "Security:IntegritySigning:CurrentKey" "至少32字节的随机密钥或其Base64值" --project .\ToDo.Razor
dotnet run --project .\ToDo.Razor
```

应用启动时会检查并应用已提交的数据库迁移。全新空库会按当前模型安全初始化；已有数据库只执行增量迁移。

## 验证

```powershell
dotnet build .\ToDo.sln -c Release
dotnet test .\ToDo.Test\ToDo.Test.csproj -c Release
dotnet publish .\ToDo.Razor\ToDo.Razor.csproj -c Release
```

运行后可检查：

- `/health/live`：进程存活。
- `/health/ready`：数据库和 Agent 自动化就绪状态（公开端点只返回状态和检查时间）。
- `/SystemHealth`：管理员查看队列、超时锁、人工关口、签名和遥测配置。
- `/DataGovernance`：管理员预览并分批执行数据留存策略。

## 生产发布

生产环境必须通过 IIS `web.config` 环境变量或安全配置中心注入数据库密码、模型 Key 和第三方 Secret。默认关闭企业微信和腾讯会议集成，配置完整后再显式启用。

- [服务器接管与发布指南](docs/服务器接管与发布指南.md)
- [生产就绪与运维指南](docs/生产就绪与运维指南.md)
- [Agent 端到端自动验收说明](docs/Agent端到端自动验收说明.md)
- [正式验收清单](docs/正式验收清单.md)

部署脚本位于 `deploy/Deploy-ToDo-Iis.ps1`，采用带时间戳的 Release 目录切换，并在数据库就绪检查失败时自动切回上一版本。数据库迁移不可由文件回滚抵消，含迁移的发布必须先做数据库备份。

## Agent 计划确认与联网搜索

新入队的普通只读任务直接执行；业务写入先规划，明确要求“先确认计划”等才等待确认。工具审批与最终验收独立：普通交付通过证据检查和独立复核后自动完成，重要变更或证据不足转人工。存量计划不批量改写。

本轮规则、代码位置、示例与验证结果以 [Agent 执行链路简化（2026-09-23）](docs/Agent执行链路简化-2026-09-23.md) 为准。联网搜索默认关闭，接入配置见 [Agent 计划与联网搜索](docs/Agent计划与联网搜索-2026-09-16.md)；其中旧的“任务一律先确认计划”流程已被本轮规则替代。
