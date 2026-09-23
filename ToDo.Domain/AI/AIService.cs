using ToDo.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace ToDo.Domain.AI
{
    /// <summary>
    /// AI服务实现类（包含任务拆分、会议纪要处理、日报生成、智能任务解析）
    /// </summary>
    public class AIService : IAIService
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _apiUrl;
        private readonly string _modelName;
        private readonly decimal _inputPricePerMillion;
        private readonly decimal _outputPricePerMillion;

        /// <summary>
        /// 构造函数（注入配置）
        /// </summary>
        /// <param name="configuration">配置项</param>
        public AIService(IConfiguration configuration)
        {
            _apiKey = configuration["AI:ApiKey"]?.Trim() ?? string.Empty;
            _apiUrl = configuration["AI:ApiUrl"]?.Trim() ?? string.Empty;
            _modelName = configuration["AI:ModelName"]?.Trim() ?? string.Empty;
            _inputPricePerMillion = decimal.TryParse(configuration["AI:InputPricePerMillion"], out var inputPrice) ? Math.Max(0, inputPrice) : 0;
            _outputPricePerMillion = decimal.TryParse(configuration["AI:OutputPricePerMillion"], out var outputPrice) ? Math.Max(0, outputPrice) : 0;

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException("AI:ApiKey 未配置");
            }

            if (string.IsNullOrWhiteSpace(_apiUrl))
            {
                throw new InvalidOperationException("AI:ApiUrl 未配置；系统不会把密钥发送到任何默认地址");
            }

            if (string.IsNullOrWhiteSpace(_modelName))
            {
                throw new InvalidOperationException("AI:ModelName 未配置");
            }

            if (!Uri.TryCreate(_apiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("AI:ApiUrl 必须是完整的 HTTPS 接口地址");
            }

            _httpClient = new HttpClient();
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        public async Task<AIMeetingMinutesResult> GenerateMeetingMinutesAsync(
            string transcript,
            string template,
            int projectId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // 限制转写文本长度，防止超长（长会议/多会议合并场景按模型128K上下文放宽到10万字符）
                var maxTranscriptLength = 100000;
                if (transcript.Length > maxTranscriptLength)
                {
                    transcript = transcript.Substring(0, maxTranscriptLength) + "\n\n（转写内容过长，已截断）";
                }

                var prompt = $@"
你是专业的会议纪要整理助手。请根据以下腾讯会议转写原文，按照指定的会议纪要模板，生成一份正式的会议纪要。

【会议纪要模板】
{template}

【转写原文】
{transcript}

【输出要求 - 必须严格遵守】
1. **必须使用 Markdown 格式输出**，包括：
   - 标题使用 #、##、### 层级
   - 表格使用 Markdown 表格语法（| 列1 | 列2 |）
   - 列表使用 - 或 1. 2. 3. 格式
   - 强调使用 **加粗**
2. 严格按照模板格式输出，不要添加模板之外的内容
3. 会议名称格式为：""**会议名称：** XXX的个人会议室 XX:XX~XX:XX — 会议主题""
4. 从转写原文中提取关键信息，填入模板对应位置
5. 参会人：从转写原文中提取真实人名，用顿号分隔
6. 会议核心主题：用一句话概括本次会议的核心议题
7. 会议整体概述：300-500字，描述会议讨论了什么、明确了什么、还有哪些待确认
8. 讨论要点：按话题分组，每个话题包含讨论内容和涉及人员
9. 会上遗留疑问：列出所有未确认的问题
10. 会议记录的待办：从转写原文中提取待办事项，包含待办内容、负责人、配合人、时间节点、备注
11. 其他补充：额外的重要信息
12. 需关注事项：需要特别关注的风险和问题
13. 如果转写原文中没有提到某个字段，填写""暂无""或""【未明确】""
14. 语言精炼、逻辑清晰
15. **只输出会议纪要 Markdown 内容，不要添加任何解释说明、代码块标记（```）或前后缀文字**";

                var response = await CallAIModelAsync(prompt, new AIChatOptions
                {
                    Temperature = 0.3,
                    MaxTokens = 8000,
                    TimeoutSeconds = 180
                }, cancellationToken);

                // 清理响应中的代码块标记（如果AI误加了）
                response = CleanMarkdownResponse(response);

                // 尝试提取标题
                var title = ExtractMeetingTitle(response);

                return new AIMeetingMinutesResult
                {
                    Success = true,
                    Content = response,
                    Title = title
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new AIMeetingMinutesResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        // 清理 AI 响应中的多余 Markdown 代码块标记
        private string CleanMarkdownResponse(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return content;

            // 移除开头的 ```markdown 或 ``` 标记
            content = content.TrimStart();
            if (content.StartsWith("```"))
            {
                var firstNewLine = content.IndexOf('\n');
                if (firstNewLine > 0)
                {
                    content = content.Substring(firstNewLine + 1);
                }
            }

            // 移除结尾的 ``` 标记
            content = content.TrimEnd();
            if (content.EndsWith("```"))
            {
                var lastNewLine = content.LastIndexOf('\n');
                if (lastNewLine > 0)
                {
                    content = content.Substring(0, lastNewLine);
                }
            }

            return content.Trim();
        }

        private string ExtractMeetingTitle(string content)
        {
            // 从 Markdown 内容中提取标题（第一行 # 开头）
            var lines = content.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("# "))
                {
                    return trimmed.Replace("# ", "").Trim();
                }
            }

            // 如果没有合适标题，使用默认标题
            return $"会议纪要_{DateTime.Now:yyyyMMdd}";
        }

        #region 任务自动拆分
        public async Task<AITaskSplitResult> SplitTaskAsync(string taskTitle, string taskDescription, string? expectedSubTaskCount = null)
        {
            try
            {
                var prompt = $"请将以下任务拆分为{expectedSubTaskCount ?? "合理数量"}个子任务，仅返回子任务列表，每行一个子任务：\n任务标题：{taskTitle}\n任务描述：{taskDescription}";
                var response = await CallAIModelAsync(prompt);
                var subTasks = response.Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                return new AITaskSplitResult { Success = true, SubTasks = subTasks };
            }
            catch (Exception ex)
            {
                return new AITaskSplitResult { Success = false, ErrorMessage = ex.Message };
            }
        }
        #endregion

        #region 【全新】会议纪要完整结构化解析（对齐任务文本解析逻辑）
        public async Task<AIMeetingFullParseResult> ProcessMeetingMinutesFullStructAsync(string meetingContent, List<string>? memberNames = null,
    List<string>? projectNames = null)
        {
            try
            {
                var memberHint = memberNames != null && memberNames.Any()
                    ? $@"
# 项目成员名单（用于匹配责任人）
以下是项目组成员的真实姓名，会议纪要中提到的人名可能有错别字或不完整，请从名单中选择最匹配的：
{string.Join("、", memberNames)}
规则：
- 如果能从名单中匹配到具体成员，填写其真实姓名
- 如果会议纪要中提到「负责人」、「项目经理」等角色指代，填写「负责人」
- 如果无法匹配，assigneeName 字段填 null

"
                        : "";
                // ===== 新增：项目归属提示 =====
                var projectHint = projectNames != null && projectNames.Count > 1
                    ? $@"
# 项目归属判定（严格执行）
本次会议关联以下项目：
{string.Join("\n", projectNames.Select(p => $"- {p}"))}

为每条任务判断它属于哪个项目，在 TaskItems 中输出 projectName 字段：
1. 必须从上面给定的项目名中选一个，严禁编造不存在的项目名；
2. 如果任务描述里明确提到某个项目名，按提到的填；
3. 如果任务描述里出现项目相关的关键词（项目简称、模块名），据此判断；
4. 如果实在无法判断，填第一个项目名；
5. 单项目会议时 projectName 统一填该唯一项目名。
"
                    : "";
                var jsonTemplate = "{\n" +
                    "  \"MeetingPurpose\": \"开会原因总结（1-2句话，简洁概括）\",\n" +
                    "  \"KeyPoints\": \"本次会议讨论了XX。会议围绕XXX展开讨论，涉及XXX方面。会议最终达成了XX结论。\",\n" +
                    "  \"Decisions\": [\n" +
                    "    {\n" +
                    "      \"content\": \"决策内容\",\n" +
                    "      \"decisionMaker\": \"结论句说话人姓名\",\n" +
                    "      \"keyQuote\": \"直接促成决策定论的最关键一句原话\",\n" +
                    "      \"originalQuotes\": [\"讨论过程的原话片段\", \"疑问与争辩的原话\", \"决策结论原话\", \"结论后的确认原话\"]\n" +
                    "    }\n" +
                    "  ],\n" +
                    "  \"TaskItems\": [\n" +
                    "    {\"title\": \"任务标题\", \"description\": \"详细说明\", \"deadline\": \"2026-08-10\", \"priority\": \"Medium\", \"status\": \"NotStarted\", \"group\": \"后端组\", \"assigneeName\": \"贺楚\", \"sourceDecisionIndex\": null}\n" +
                    "  ]\n" +
                    "}";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("你是专业项目管理助理，请先完整阅读并理解下面全部的会议纪要内容，然后再进行分析总结和任务提取。严禁中途截断或省略内容。");
                sb.AppendLine("【工作流程】：");
                sb.AppendLine("第一步：完整通读全部会议纪要，掌握会议的整体脉络和全部细节。");
                sb.AppendLine("第二步：基于完整的会议内容，生成开会原因总结（MeetingPurpose）、AI摘要（KeyPoints）、会议决策（Decisions）。");
                sb.AppendLine("第三步：从完整的会议内容中提取所有待办任务（TaskItems），并判断每条任务是否来源于某条决策（填写 sourceDecisionIndex）。");
                sb.AppendLine("第四步：检查所有输出内容是否完整，确保没有任何句子以省略号或未完成状态结束。");
                sb.AppendLine();
                sb.AppendLine(memberHint);
                sb.AppendLine(projectHint);
                sb.AppendLine("# 任务类型识别规则");
                sb.AppendLine("请根据以下规则标记每个任务：");
                sb.AppendLine("1. NewTask（新增任务）：会议中首次提到的任务，之前没有对应记录，需要创建新任务");
                sb.AppendLine("2. TaskChange（历史任务变更）：会议中提到的任务与已有任务匹配，但状态/负责人/截止时间/优先级有变化，需要更新已有任务");
                sb.AppendLine("   - 如果会议中提到某个任务的状态变了（如：已完成→进行中）→ 标记为 TaskChange");
                sb.AppendLine("   - 如果会议中提到某个任务的负责人变了 → 标记为 TaskChange");
                sb.AppendLine("   - 如果会议中提到某个任务的截止时间变了 → 标记为 TaskChange");
                sb.AppendLine("   - 如果会议中提到某个任务的优先级变了 → 标记为 TaskChange");
                sb.AppendLine("3. 如果无法确定是新增还是变更，默认标记为 NewTask");
                sb.AppendLine("# 任务识别规则（严格执行）");
                sb.AppendLine("一条内容如果满足以下任一特征，就视为待办任务：");
                sb.AppendLine("1. 明确某人要做某事（如：贺楚要完成XX、张三负责XX）");
                sb.AppendLine("2. 有明确的执行动作+时间要求（如：本周五完成、周三输出、下周评估）");
                sb.AppendLine("3. 提到需要交付的产出物（如：提供版本、输出方案、完成统计）");
                sb.AppendLine();
                sb.AppendLine("# 正例（这些都应该识别为任务）");
                sb.AppendLine("- 贺楚明确：后端本周完成临界点规则修正 → 任务：修正临界点规则，责任人：贺楚");
                sb.AppendLine("- 8月5日提供自测版本 → 任务：提供自测版本");
                sb.AppendLine("- 周三输出初步技术方案评审 → 任务：输出技术方案评审");
                sb.AppendLine("- 周五落地初稿 → 任务：落地初稿");
                sb.AppendLine("- 负责人安排下周完成存储用量统计评估 → 任务：存储用量统计评估");
                sb.AppendLine("- 决策确定「会议准备页改为三个列表展示」→ 落地执行需要改造页面 → 任务：改造会议准备页任务展示，sourceDecisionIndex 填该决策序号");
                sb.AppendLine();
                sb.AppendLine("# 反例（这些不应识别为任务）");
                sb.AppendLine("- 本次会议暂不展开讨论 → 这是说明，不是任务");
                sb.AppendLine("- 纯讨论过程、纯表态（如「嗯」「明白了」「这个方向可以」）→ 不是任务");
                sb.AppendLine("- 纯结论性决策且不需要任何后续执行动作（如「暂不设置紧急截止时间，可放慢节奏自行学习」）→ 只作为决策记录，不生成任务");
                sb.AppendLine();
                sb.AppendLine("# 决策落地任务识别规则（sourceDecisionIndex）");
                sb.AppendLine("决策本身是「结论」，但一部分任务正是来源于这些决策的落地执行。提取任务时必须逐条判断：");
                sb.AppendLine("1. 如果决策的落地需要有人做具体工作（如：改造/调整页面或规则、输出方案或文档、排期执行、按决定推进某事项），必须把该执行工作提取为任务，并将 sourceDecisionIndex 填为该决策在 Decisions 数组中的序号（从1开始）。");
                sb.AppendLine("2. 任务在会议讨论中直接布置、与任何决策都没有派生关系时，sourceDecisionIndex 填 null。");
                sb.AppendLine("3. 一条决策可以派生多条任务（每条任务都填同一个决策序号）；一条任务只来源于一条决策，填写最直接对应的那条。");
                sb.AppendLine("4. 序号必须严格对应本次输出的 Decisions 数组顺序，严禁编造不存在的序号。");
                sb.AppendLine();
                sb.AppendLine("# 字段强制规则");
                sb.AppendLine("1. title：任务短标题，动词+核心对象，严格控制8-20个汉字，禁止大段复制原文；完整上下文、细节全部放到description字段，不要塞到title中。");
                sb.AppendLine("2. description：任务详细说明，补充会议上下文，完整还原任务背景细节。");
                sb.AppendLine("3. deadline截止日期规则：");
                sb.AppendLine("   - 文本出现 本周五、下周一、3天内、12月20日前 等口语时间，自动换算为标准 yyyy-MM-dd");
                sb.AppendLine("   - 无法推算具体日期填 null");
                sb.AppendLine($"   - 假设当前系统日期：{AppTime.Now:yyyy-MM-dd}");
                sb.AppendLine("4. priority优先级规则：");
                sb.AppendLine("   - High：紧急重要；Medium：常规任务；Low：低优先级跟进事项，默认Medium");
                sb.AppendLine("5. status状态规则：");
                sb.AppendLine("   - 默认全部 NotStarted；只有明确说已完成才填 Completed；进行中填 InProgress");
                sb.AppendLine("6. group分组规则：提到前端、后端、测试、产品、运维等部门/小组填入group，无则null");
                sb.AppendLine("7. assigneeName责任人：提取人名，无明确责任人填null");
                sb.AppendLine("8. 过滤&去重规则：只输出真实待办行动项，纯讨论内容、纯表态不生成任务；决策结论本身不重复生成任务，但决策落地所必需的执行工作必须生成任务并标注 sourceDecisionIndex；语义完全一致的重复任务做去重合并，只保留一条。");
                sb.AppendLine();
                sb.AppendLine("# 会议决策识别规则");
                sb.AppendLine("从会议纪要中提取所有明确做出的决策，包括：");
                sb.AppendLine("1. 方案确定（如：决定采用XX方案、确定XX技术路线）");
                sb.AppendLine("2. 方向确认（如：确认下一步往XX方向发展）");
                sb.AppendLine("3. 规则制定（如：明确XX规则为XX）");
                sb.AppendLine("4. 资源分配（如：决定由XX负责XX模块）");
                sb.AppendLine();
                sb.AppendLine("# 决策字段要求（严格执行）");
                sb.AppendLine("每条决策包含四个字段：");
                sb.AppendLine("- content：决策的简要内容（总结归纳，不必是原文）");
                sb.AppendLine("- decisionMaker：决策拍板人【禁止留空】——优先取决策结论句的说话人（转写原文中\"姓名：\"前缀标明了说话人），其次取主导该讨论并给出最终方案的人；多人共同拍板用顿号并列（如\"乔宽、赵冬\"）；严禁留空、严禁照抄占位符或写\"决策人\"字样；只有原文完全无法判断由谁拍板时才填\"未明确\"并在括号内说明原因，如\"未明确（结论由多人附和，无法确定主导者）\"");
                sb.AppendLine("- keyQuote：该决策【最关键的一句原话】——讨论中直接促成拍板定论的那句话（明确的结论表态、方案拍板句），必须逐字摘自原文，严禁编造；如果原文没有明确的定论句，取与决策结论最接近的原话");
                sb.AppendLine("- originalQuotes：【最重要】该决策相关讨论的全部过程原文片段，不是仅保存决策结论单句；严禁自行编造或改写！");
                sb.AppendLine();
                sb.AppendLine("# originalQuotes 填写规则（严格执行）：");
                sb.AppendLine("0. 【标注目标】把该决策从话题提出到定论的全部讨论过程都标注出来，不遗漏任何相关发言；可以是讨论所在的一整块连续上下文，也可以按发言顺序拆成多个片段（仅当讨论中间穿插了明显无关内容时才拆分）；");
                sb.AppendLine("1. 【锚点】以该决策结论对应的转写句子作为中心点；");
                sb.AppendLine("2. 【片段范围】完整覆盖：结论形成前的多轮讨论、疑问、争辩全过程 + 结论达成后的简短确认对话（如\"对\"\"好，就这么定\"等认可性回应）；");
                sb.AppendLine("3. 【真实性】每个片段按原文顺序逐字摘抄，禁止改写、缩写、总结；保留说话人前缀（如\"乔宽：\"），与原文完全一致；所有片段按讨论先后顺序排列，结论句放最后一位；");
                sb.AppendLine("4. 如果原文中没有与该决策直接对应的语句，则 originalQuotes 填空数组、keyQuote 填空字符串，不要编造。");
                sb.AppendLine();
                sb.AppendLine("# KeyPoints 摘要生成规则（简洁连贯）");
                sb.AppendLine("KeyPoints 必须是一段或两段连贯的完整段落，总长度控制在 200-400 字，要求：");
                sb.AppendLine("1. 用简洁的段落描述，不要分点、不要换行过多；");
                sb.AppendLine("2. 第一句概述会议主题和目的，中间说明核心讨论内容，最后总结会议结论；");
                sb.AppendLine("3. 整个摘要必须是完整的连贯文本，严禁出现省略号或未完成的句子；");
                sb.AppendLine("4. 不要包含待办事项或任务列表。");
                sb.AppendLine();
                sb.AppendLine("# 输出格式");
                sb.AppendLine("每条任务包含：title(短标题)、description(详细说明)、deadline(截止日期yyyy-MM-dd)、priority(High/Medium/Low)、status(NotStarted/InProgress/Completed)、group(分组)、assigneeName(责任人姓名)、sourceDecisionIndex(来源决策序号，从1开始；不来源于决策填null)");
                sb.AppendLine();
                sb.AppendLine("# 只输出纯JSON格式，不要任何解释、markdown、前言后语，结构如下：");
                sb.AppendLine(jsonTemplate);
                sb.AppendLine();
                sb.AppendLine("会议原文内容：");
                sb.AppendLine(meetingContent);
                var prompt = sb.ToString();

                var response = await CallAIModelAsync(prompt, new AIChatOptions { Temperature = 0.2, MaxTokens = 64000, TimeoutSeconds = 600 }, CancellationToken.None);

                // 截取JSON块，和ParseTaskTextAsync一致容错处理
                var jsonStart = response.IndexOf('{');
                var jsonEnd = response.LastIndexOf('}');
                if (jsonStart < 0 || jsonEnd <= jsonStart)
                {
                    throw new Exception("AI返回无法提取合法JSON结构");
                }
                var jsonRaw = response.Substring(jsonStart, jsonEnd - jsonStart + 1);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                AIMeetingFullParseResult? result;
                try
                {
                    result = JsonSerializer.Deserialize<AIMeetingFullParseResult>(jsonRaw, options);
                }
                catch (System.Text.Json.JsonException)
                {
                    // AI输出被max_tokens截断时，JSON会带有未闭合的对象/数组——
                    // 回退到最后一个完整元素并补齐括号，抢救已完成的部分，而不是整体解析失败
                    if (!TryRepairTruncatedJson(jsonRaw, out var repaired))
                        throw;
                    System.Diagnostics.Debug.WriteLine("[AI Warning] 检测到JSON输出被截断，已自动修复并丢弃未完成的尾部内容");
                    result = JsonSerializer.Deserialize<AIMeetingFullParseResult>(repaired, options);
                }

                if (result == null)
                    throw new Exception("JSON反序列化失败");

                result.Success = true;
                result.ErrorMessage = string.Empty;
                return result;
            }
            catch (Exception ex)
            {
                return new AIMeetingFullParseResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// 修复被 max_tokens 截断的AI输出JSON：扫描括号配对，回退到最后一个完整闭合的
        /// 根对象内元素边界，再按栈补齐未闭合的括号。无法定位安全边界时返回 false。
        /// </summary>
        internal static bool TryRepairTruncatedJson(string json, out string repaired)
        {
            repaired = string.Empty;
            if (string.IsNullOrWhiteSpace(json) || json[0] != '{') return false;

            var stack = new List<char>();   // 未闭合括号栈（{ 或 [）
            var inString = false;
            var escaped = false;
            var lastSafeCut = -1;           // 最近一个"根对象内完整元素"结束位置（闭括号后一位）
            char[]? lastSafeStack = null;   // 安全边界当时的括号栈；不能使用扫描结束后的栈

            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{' || c == '[') { stack.Add(c); continue; }
                if (c != '}' && c != ']') continue;

                if (stack.Count == 0) return false;
                var open = stack[^1];
                if ((c == '}' && open != '{') || (c == ']' && open != '[')) return false;
                stack.RemoveAt(stack.Count - 1);

                // 安全边界：刚闭合的是顶层属性值（栈剩根对象），或根对象直接数组的最后一个完整元素
                if (stack.Count == 1 || (stack.Count == 2 && stack[1] == '['))
                {
                    lastSafeCut = i + 1;
                    lastSafeStack = stack.ToArray();
                }
            }

            if (lastSafeCut <= 0 || lastSafeStack == null || lastSafeStack.Length == 0) return false;

            var closer = new char[lastSafeStack.Length];
            for (var i = 0; i < lastSafeStack.Length; i++)
            {
                closer[lastSafeStack.Length - 1 - i] = lastSafeStack[i] == '{' ? '}' : ']';
            }
            repaired = json.Substring(0, lastSafeCut) + new string(closer);

            // 该方法的契约是：返回 true 时必须得到语法有效的 JSON，避免把错误延迟到业务反序列化阶段。
            try
            {
                using var document = JsonDocument.Parse(repaired);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                repaired = string.Empty;
                return false;
            }
        }
        #endregion
        #region 通用AI对话
        public async Task<string> GetChatCompletionAsync(string prompt)
        {
            try
            {
                return await CallAIModelAsync(prompt);
            }
            catch (Exception ex)
            {
                throw new Exception($"AI对话调用失败：{ex.Message}");
            }
        }

        public async Task<string> GetChatCompletionAsync(
            string prompt,
            AIChatOptions options,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await CallAIModelAsync(prompt, options, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"AI 对话超过 {options.TimeoutSeconds} 秒未完成");
            }
            catch (Exception ex)
            {
                throw new Exception($"AI对话调用失败：{ex.Message}", ex);
            }
        }

        public async Task<AICompletionResult> GetChatCompletionWithUsageAsync(
            string prompt,
            AIChatOptions options,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await CallAIModelWithUsageAsync(prompt, options, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"AI 对话超过 {options.TimeoutSeconds} 秒未完成");
            }
            catch (Exception ex)
            {
                throw new Exception($"AI对话调用失败：{ex.Message}", ex);
            }
        }
        #endregion
        #region 会议纪要处理【旧版兜底保留】
        public async Task<AIMeetingSummaryResult> ProcessMeetingMinutesAsync(string meetingContent)
        {
            try
            {
                var prompt = $"请处理以下会议纪要，提取：1. 核心要点；2. 待办事项（含责任人、截止时间），返回JSON格式：\n{meetingContent}";
                var response = await CallAIModelAsync(prompt);
                var result = JsonSerializer.Deserialize<AIMeetingSummaryResult>(response);
                result!.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                return new AIMeetingSummaryResult { Success = false, ErrorMessage = ex.Message };
            }
        }
        #endregion

        #region 基础日报生成
        public async Task<AIDailyReportResult> GenerateDailyReportAsync(AIDailyReportInput input)
        {
            try
            {
                var prompt = $"请根据以下当日项目变化，生成一份结构化日报：\n已完成任务：{string.Join("、", input.CompletedTasks)}\n未完成任务：{string.Join("、", input.UncompletedTasks)}\n会议与资料动态：{string.Join("、", input.ActivityNotes)}\n问题：{string.Join("、", input.Problems)}\n日期：{input.ReportDate:yyyy-MM-dd}";
                var response = await CallAIModelAsync(prompt, new AIChatOptions { Temperature = 0.2, MaxTokens = 16000, TimeoutSeconds = 180 }, CancellationToken.None);
                return new AIDailyReportResult { Success = true, GeneratedReport = response };
            }
            catch (Exception ex)
            {
                return new AIDailyReportResult { Success = false, ErrorMessage = ex.Message };
            }
        }
        #endregion

        #region 按项目分类的每日汇报生成
        public async Task<AIDailyReportResult> GenerateDailyReportByProjectAsync(DailyReportByProjectInput input)
        {
            try
            {
                var prompt = BuildProjectDailyReportPrompt(input);
                var aiResponse = await CallAIModelAsync(prompt, new AIChatOptions { Temperature = 0.2, MaxTokens = 16000, TimeoutSeconds = 180 }, CancellationToken.None);

                return new AIDailyReportResult
                {
                    Success = true,
                    GeneratedReport = aiResponse
                };
            }
            catch (Exception ex)
            {
                return new AIDailyReportResult
                {
                    Success = false,
                    ErrorMessage = $"生成每日汇报失败：{ex.Message}"
                };
            }
        }
        #endregion

        #region 团队汇报生成（基于个人日报汇总）
        /// <summary>
        /// 基于各成员个人日报按项目汇总生成团队汇报
        /// </summary>
        public async Task<AIDailyReportResult> GenerateTeamReportAsync(TeamReportInput input)
        {
            try
            {
                var prompt = BuildTeamReportPrompt(input);
                var response = await CallAIModelAsync(prompt, new AIChatOptions { Temperature = 0.2, MaxTokens = 16000, TimeoutSeconds = 180 }, CancellationToken.None);
                return new AIDailyReportResult { Success = true, GeneratedReport = response };
            }
            catch (Exception ex)
            {
                return new AIDailyReportResult
                {
                    Success = false,
                    ErrorMessage = $"生成团队汇报失败：{ex.Message}"
                };
            }
        }

        private static string BuildTeamReportPrompt(TeamReportInput input)
        {
            var sb = new StringBuilder();
            var withReport = input.Members.Where(m => m.HasDailyReport).ToList();
            var withoutReport = input.Members.Where(m => !m.HasDailyReport)
                .Select(m => m.UserName).Distinct(StringComparer.Ordinal).ToList();
            var hasTaskGroups = input.TaskGroups != null && input.TaskGroups.Count > 0;

            sb.AppendLine($"【{input.ReportDate:yyyy-MM-dd} 团队汇报】");
            sb.AppendLine($"项目：{input.ProjectName}｜负责人：{input.ProjectLeader}");
            sb.AppendLine($"项目总成员数：{input.Members.Select(m => m.UserName).Distinct(StringComparer.Ordinal).Count()}");
            sb.AppendLine($"已提交日报人数：{withReport.Select(m => m.UserName).Distinct(StringComparer.Ordinal).Count()}");
            sb.AppendLine($"未提交日报人数：{withoutReport.Count}");
            if (withoutReport.Count > 0)
                sb.AppendLine($"未提交日报成员名单：{string.Join("、", withoutReport)}");
            if (hasTaskGroups)
                sb.AppendLine($"项目任务组数：{input.TaskGroups!.Count}（需按任务组单独汇总）");
            sb.AppendLine();

            sb.AppendLine("【严格输出规则 - 违反任一规则即视为不合格】");
            sb.AppendLine("【格式铁律（必须严格遵守）】每个大板块标题必须单独占一行，板块之间必须换行分隔，绝对不能把下一个板块的标题直接跟在上一个板块内容的同一行末尾！");
            if (hasTaskGroups)
                sb.AppendLine("1. 输出顺序：团队整体进展 → 任务组汇总 → 各成员工作明细 → 风险与问题");
            else
                sb.AppendLine("1. 输出顺序：团队整体进展 → 各成员工作明细 → 风险与问题");
            sb.AppendLine("【防重复 严格铁律（违反即不合格）】");
            sb.AppendLine("   - 团队整体进展：只写高层统计数字 + 一句话总体结论（如「今日新增N个任务组、完成N个任务、M人提交日报」）；**禁止在这里粘贴具体任务名、任务组名、成员个人工作内容**，也禁止把后面『任务组汇总』『各成员工作明细』的原文提前在这里复述。");
            sb.AppendLine("   - 任务组汇总：只写每个任务组的『原定目标 + 完成/总数 + 整体进度 + 组内任务清单快照』；**绝对不要再写『组内各负责人工作：XX - 干了啥』**，成员个人工作全部放到『各成员工作明细』唯一出口，避免和成员明细重复。");
            sb.AppendLine("   - 各成员工作明细：只写成员本人今天的真实动作（做了啥、改了啥、新建了啥），**不再复述任务组的原定目标/整体进度统计数字**。任务组的进度属于任务组汇总板块，成员个人属于成员明细板块，二者互斥。");
            sb.AppendLine("   - 任何一件具体事实（如「新建任务组#2 小组1」「贺楚新建任务组」）只能在 团队整体进展 / 任务组汇总 / 各成员工作明细 三者其中一个位置出现一次，绝不能两个地方重复写。");
            sb.AppendLine("2. 团队整体进展：精简3行以内，总结当日团队整体推进的核心事项。必须以「团队整体进展：」开头，单独成段");
            if (hasTaskGroups)
            {
                sb.AppendLine("3. 任务组汇总（该项目设有任务组，必须写）：以「任务组汇总：」为开头标题，单独占一行，然后按任务组逐个总结，格式如下：");
                sb.AppendLine("   【XX组】原定目标：XXX；本组进度：已完成X/总任务X，整体进度X%");
                sb.AppendLine("   然后在任务组名下追加组内任务清单（按下面「任务组快照」的原始数据列出即可）。若该任务组暂无任务，直接写「暂无任务」。");
                sb.AppendLine("   【禁止】不要再写「组内各负责人工作：负责人A - 干了啥」这种成员个人内容，这一块属于后面的『各成员工作明细』。");
            }
            int memberRuleNo = hasTaskGroups ? 4 : 3;
            int riskRuleNo = memberRuleNo + 1;
            sb.AppendLine($"{memberRuleNo}. 各成员工作明细（最高优先级规则）：以「各成员工作明细：」为开头标题，单独占一行，然后换行列出每一位成员。该项目共有 {input.Members.Select(m => m.UserName).Distinct(StringComparer.Ordinal).Count()} 位成员，你必须逐个列出以下完整名单中的每一位，名单：{string.Join("、", input.Members.Select(m => m.UserName).Distinct(StringComparer.Ordinal))}");
            sb.AppendLine("   格式：每条单独一行，前缀用「•」或「-」，即：- 成员名：核心工作内容（精简到40字内）");
            sb.AppendLine("   - 对于今日已提交个人日报的成员，列出其实际工作内容（新建任务、更新任务、新建任务组、发表评论、新建会议纪要、创建/接收行动项、上传资料、提交日报等个人动作），不要复述任务组的原定目标或整体进度。");
            sb.AppendLine("   - 对于未提交日报的成员，必须注明「今日未提交日报，暂无工作变动」字样，并放在列表最后。即使只有一人未提交也必须明确写出该人的名字，绝不可省略不写");
            sb.AppendLine($"{riskRuleNo}. 风险与问题（必须主动挖掘，不要轻易写暂无）：必须以「风险与问题：」为开头标题，单独占一行，绝不能跟在成员内容末尾！然后从各成员工作内容和任务组进度中识别汇总以下风险信号，即使没明确写「问题」也要提炼：");
            sb.AppendLine("   - 进度异常：如进度0%、进度严重滞后、进行中但长期无进展等（例：「任务#37进度0%，需关注」）");
            sb.AppendLine("   - 时间风险：如截止时间未设置、临近截止但进度不足、已逾期等（例：「任务#37截止时间未设置，存在进度不可控风险」）");
            sb.AppendLine("   - 协作卡点：如需他人审核、依赖外部资源、信息不明确等（例：「XX方案待负责人审核确认」）");
            sb.AppendLine("   - 明确写出的问题、风险、待办、阻塞等关键词");
            sb.AppendLine("   - 未提交日报的成员人数较多时，也需作为风险注明（例：「今日2人未提交日报，需督促提交」）");
            sb.AppendLine("   格式：每条风险单独一行，前缀用「•」或「-」；确实完全没有任何风险信号时才写「暂无」，绝不能偷懒");
            sb.AppendLine("   【禁止】风险里只写风险本身的判断（进度0%、截止未设置等），不要再复述成员已经做过的具体动作，避免再次重复。");
            sb.AppendLine($"{riskRuleNo + 1}. 不输出多余空行，不包含待办列表或任务清单");
            sb.AppendLine();

            if (hasTaskGroups)
            {
                sb.AppendLine("任务组快照（请据此按任务组汇总，原始数据如下）：");
                sb.AppendLine("---------------------------------------");
                foreach (var g in input.TaskGroups!)
                {
                    sb.AppendLine($"任务组：{g.GroupName}");
                    if (!string.IsNullOrWhiteSpace(g.GroupDescription))
                        sb.AppendLine($"  原定目标：{g.GroupDescription}");
                    sb.AppendLine($"  任务统计：总数{g.TotalTaskCount}｜已完成{g.CompletedTaskCount}｜整体进度{g.OverallProgressPercent}%");
                    if (g.TaskSnapshots.Count > 0)
                    {
                        sb.AppendLine("  组内任务清单（任务名｜状态｜进度%｜负责人）：");
                        foreach (var s in g.TaskSnapshots)
                            sb.AppendLine($"    - {s}");
                    }
                    else
                    {
                        sb.AppendLine("  组内任务清单：暂无任务");
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("---------------------------------------");
                sb.AppendLine();
            }

            sb.AppendLine("各成员个人日报摘要：");
            sb.AppendLine("---------------------------------------");
            foreach (var member in input.Members)
            {
                sb.Append(member.HasDailyReport ? "【已提交】" : "【未提交】");
                sb.Append($" 成员：{member.UserName}");
                sb.Append(member.HasDailyReport ? "（已提交）" : "（未提交日报）");
                sb.AppendLine();
                if (member.HasDailyReport)
                {
                    if (!string.IsNullOrWhiteSpace(member.ProjectSummary))
                    {
                        var summary = member.ProjectSummary.Length > 200
                            ? member.ProjectSummary.Substring(0, 200) + "..."
                            : member.ProjectSummary;
                        sb.AppendLine($"项目分项：{summary}");
                    }
                    else if (!string.IsNullOrWhiteSpace(member.TotalSummary))
                    {
                        var summary = member.TotalSummary.Length > 200
                            ? member.TotalSummary.Substring(0, 200) + "..."
                            : member.TotalSummary;
                        sb.AppendLine($"个人日报：{summary}");
                    }
                    else
                    {
                        sb.AppendLine("（无内容）");
                    }
                }
                else
                {
                    sb.AppendLine("今日未提交日报，暂无工作变动");
                }
                sb.AppendLine();
            }
            sb.AppendLine("---------------------------------------");

            return sb.ToString();
        }
        #endregion

        #region 智能解析任务文本
        public async Task<AITaskParseResult> ParseTaskTextAsync(string text, string? expectedCount = null)
        {
            try
            {
                var prompt = $@"
请将以下任务描述文本解析为多个子任务。每个子任务应包含：
- 任务名称（title）：简洁明确的标题
- 任务描述（description）：详细说明，可基于原文扩展
- **截止时间（deadline）**：格式为 yyyy-MM-dd
  - **重要规则**：如果文本中明确提到时间信息（如""本周五""、""12月25日前""、""下周一""、""3天内""等），必须转换为具体的日期格式
  - 如果文本中有明确的时间表述但无法确定具体日期，请根据当前日期推算（假设当前日期为 {AppTime.Now:yyyy-MM-dd}）
  - 如果没有明确的时间信息，则返回 null
- 优先级（priority）：High/Medium/Low，根据任务重要性、紧急程度评估
- 状态（status）：NotStarted/InProgress/Completed，默认 NotStarted
- **分组（group）**：如果文本中明确提到分组信息则提取，否则留空

文本内容：
{text}

期望拆分数量：{(string.IsNullOrEmpty(expectedCount) ? "由AI自动判断" : expectedCount)}

请以JSON数组格式返回，格式如下：
[
  {{
    ""title"": ""任务名称"",
    ""description"": ""任务详细描述"",
    ""deadline"": ""2024-12-31"",  // 如果有明确时间则填写具体日期，否则填 null
    ""priority"": ""Medium"",
    ""status"": ""NotStarted"",
    ""group"": ""前端组""
  }}
]

**重要**：
1. deadline 字段：有明确时间信息时必须填写具体日期（yyyy-MM-dd），没有则填 null
2. 只返回JSON数组，不要包含其他说明文字。";

                var response = await CallAIModelAsync(prompt);

                // 尝试提取JSON
                var jsonStart = response.IndexOf('[');
                var jsonEnd = response.LastIndexOf(']');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                    var items = JsonSerializer.Deserialize<List<TaskParseItem>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (items != null && items.Any())
                    {
                        // 验证截止时间格式
                        foreach (var item in items)
                        {
                            if (!string.IsNullOrEmpty(item.Deadline) && !DateTime.TryParse(item.Deadline, out _))
                            {
                                // 如果格式不正确，尝试修复或置空
                                item.Deadline = string.Empty;
                            }
                        }

                        return new AITaskParseResult
                        {
                            Success = true,
                            Items = items
                        };
                    }
                }

                return new AITaskParseResult
                {
                    Success = false,
                    ErrorMessage = "无法解析AI返回结果"
                };
            }
            catch (Exception ex)
            {
                return new AITaskParseResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        #endregion

        #region 私有辅助方法
        /// <summary>
        /// 构造按项目汇总的AI提示词
        /// </summary>
        private string BuildProjectDailyReportPrompt(DailyReportByProjectInput input)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【{input.ReportDate:yyyy-MM-dd} 项目工作汇总】");
            sb.AppendLine();

            if (!input.ProjectDailyDatas.Any())
            {
                sb.AppendLine("今日无项目更新");
                return sb.ToString();
            }

            sb.AppendLine("【严格输出规则】");
            sb.AppendLine("1. 每个项目顺序：项目基础信息 → 项目小结 → 任务进展 → 会议纪要 → 项目日报");
            sb.AppendLine("2. 项目小结：**必须精简到3行以内**，项目描述只保留核心目标，禁止粘贴完整文档；");
            sb.AppendLine("   - 今日新建且无数据：写「项目「{项目名}」今日新建，描述：「{项目描述}」，暂无任务/会议/日报」");
            sb.AppendLine("   - 有数据：总结真实发生的动作、任务、会议、日报");
            sb.AppendLine("3. 任务进展：只展示今日真实存在的任务，**包含任务描述（精简到40字内）**，格式固定：- 任务名 | 状态 | 负责人 | 描述：xxx");
            sb.AppendLine("4. 会议/日报保留核心内容，2行以内");
            sb.AppendLine("5. 最终只保留项目片段，不输出多余空行");
            sb.AppendLine();

            foreach (var project in input.ProjectDailyDatas)
            {
                bool isNew = project.ProjectCreateTime.Date == input.ReportDate.Date;
                bool hasUpdate = isNew || project.NewTasks.Any() || project.CompletedTasks.Any() || project.NewMeetings.Any() || project.NewReports.Any();
                if (!hasUpdate) continue;

                sb.AppendLine($"项目：{project.ProjectName}（{(isNew ? "今日新建" : "今日更新")}）");
                sb.AppendLine($"负责人：{project.ProjectLeader}");
                sb.AppendLine();

                // 项目小结（精简版）
                sb.AppendLine("项目小结：");
                if (isNew)
                {
                    if (string.IsNullOrWhiteSpace(project.ProjectDescription))
                    {
                        sb.AppendLine($"- 项目「{project.ProjectName}」今日新建，暂无项目描述。");
                    }
                    else
                    {
                        var shortDesc = project.ProjectDescription.Length > 50
                            ? project.ProjectDescription.Substring(0, 50) + "..."
                            : project.ProjectDescription;
                        sb.AppendLine($"- 项目「{project.ProjectName}」今日新建，核心目标：{shortDesc}");
                    }

                    var updates = new List<string>();
                    if (project.NewTasks.Any()) updates.Add("新增任务");
                    if (project.CompletedTasks.Any()) updates.Add("完成任务");
                    if (project.NewMeetings.Any()) updates.Add("新增会议");
                    if (project.NewReports.Any()) updates.Add("新增日报");

                    if (updates.Any())
                    {
                        sb.AppendLine($"- 今日同步更新：{string.Join("、", updates)}。");
                    }
                    else
                    {
                        sb.AppendLine("- 暂无任务/会议/日报更新。");
                    }
                }
                else // 历史项目
                {
                    var updates = new List<string>();
                    if (project.NewTasks.Any()) updates.Add("新增任务");
                    if (project.CompletedTasks.Any()) updates.Add("完成任务");
                    if (project.NewMeetings.Any()) updates.Add("新增会议纪要");
                    if (project.NewReports.Any()) updates.Add("新增项目日报");

                    var shortDesc = string.IsNullOrWhiteSpace(project.ProjectDescription)
                        ? "无核心描述"
                        : (project.ProjectDescription.Length > 30 ? project.ProjectDescription.Substring(0, 30) + "..." : project.ProjectDescription);

                    sb.AppendLine($"- 项目核心：{shortDesc} | 今日更新：{string.Join("、", updates)}。");
                }
                sb.AppendLine();

                // 任务进展（去重 + 精简描述）
                sb.AppendLine("📌 任务进展：");
                var uniqueTasks = project.NewTasks
                    .Concat(project.CompletedTasks)
                    .GroupBy(t => t.TaskTitle)
                    .Select(g => g.First())
                    .Take(3)
                    .ToList();

                if (uniqueTasks.Any())
                {
                    foreach (var t in uniqueTasks)
                    {
                        var shortDesc = string.IsNullOrWhiteSpace(t.TaskDescription)
                            ? "无描述"
                            : (t.TaskDescription.Length > 40 ? t.TaskDescription.Substring(0, 40) + "..." : t.TaskDescription);

                        sb.AppendLine($"- {t.TaskTitle} | 状态：{t.Status} | 负责人：{t.AssigneeName} | 描述：{shortDesc}");
                    }
                }
                else
                {
                    sb.AppendLine("- 暂无任务");
                }
                sb.AppendLine();

                // 会议纪要
                sb.AppendLine("📝 会议纪要：");
                if (project.NewMeetings.Any())
                {
                    foreach (var m in project.NewMeetings.Take(1))
                    {
                        var shortContent = m.MeetingContent.Length > 60 ? m.MeetingContent.Substring(0, 60) + "..." : m.MeetingContent;
                        sb.AppendLine($"- {m.MeetingTitle}：{shortContent}");
                    }
                }
                else
                {
                    sb.AppendLine("- 暂无会议");
                }
                sb.AppendLine();

                // 项目日报
                sb.AppendLine("📄 项目日报：");
                if (project.NewReports.Any())
                {
                    foreach (var r in project.NewReports.Take(1))
                    {
                        var shortContent = r.ReportContent.Length > 60 ? r.ReportContent.Substring(0, 60) + "..." : r.ReportContent;
                        sb.AppendLine($"- {r.ReportTitle}：{shortContent}");
                    }
                }
                else
                {
                    sb.AppendLine("- 暂无日报");
                }
                sb.AppendLine();
                sb.AppendLine("---------------------------------------");
            }

            sb.AppendLine("📊 整体总总结：");
            sb.AppendLine($"今日共汇总 {input.ProjectDailyDatas.Count(p => p.ProjectCreateTime.Date == input.ReportDate.Date)} 个新建项目，{input.ProjectDailyDatas.Count(p => p.NewTasks.Any() || p.CompletedTasks.Any() || p.NewMeetings.Any() || p.NewReports.Any())} 个更新项目。");
            return sb.ToString();
        }

        /// <summary>
        /// 通用AI模型调用方法（优化参数：低温度+最大令牌）
        /// </summary>
        private Task<string> CallAIModelAsync(string prompt)
        {
            return CallAIModelAsync(prompt, null, CancellationToken.None);
        }

        private async Task<string> CallAIModelAsync(
            string prompt,
            AIChatOptions? options,
            CancellationToken cancellationToken)
        {
            return (await CallAIModelWithUsageAsync(prompt, options, cancellationToken)).Content;
        }

        private async Task<AICompletionResult> CallAIModelWithUsageAsync(
            string prompt,
            AIChatOptions? options,
            CancellationToken cancellationToken)
        {
            var modelName = string.IsNullOrWhiteSpace(options?.ModelName) ? _modelName : options.ModelName.Trim();
            var temperature = options?.Temperature ?? 0.3;
            var maxTokens = options?.MaxTokens ?? 12000;
            var timeoutSeconds = Math.Clamp(options?.TimeoutSeconds ?? 90, 10, 600);
            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = modelName,
                ["messages"] = new[] { new { role = "user", content = prompt } },
                ["temperature"] = temperature,
                ["max_tokens"] = maxTokens
            };

            if (options?.Tools.Count > 0)
            {
                requestBody["tools"] = options.Tools.Select(tool => new
                {
                    type = "function",
                    function = new
                    {
                        name = tool.Name,
                        description = tool.Description,
                        parameters = tool.Parameters
                    }
                }).ToArray();
                requestBody["tool_choice"] = "auto";
            }

            var json = JsonSerializer.Serialize(requestBody);

            // DeepSeek 偶发返回 HTTP 200 但 message.content 为空（服务端波动/内容过滤），
            // 遇到空响应最多重试 2 次（共 3 次请求），间隔 1 秒；仍失败时给出友好提示。
            const int maxAttempts = 3;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.PostAsync(_apiUrl, content, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"AI 接口请求超过 {timeoutSeconds} 秒未完成，请稍后重试或缩减会议内容长度");
                }

                using (response)
                {
                    var resultJson = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = resultJson.Length > 1000 ? resultJson[..1000] : resultJson;
                        throw new InvalidOperationException($"AI 接口请求失败：{(int)response.StatusCode} {response.ReasonPhrase}；地址：{_apiUrl}；服务端返回：{detail}");
                    }

                    using var result = JsonDocument.Parse(resultJson);
                    if (!result.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                        throw new InvalidOperationException("AI 接口返回中没有 choices 字段，请检查 ApiUrl 和 ModelName 是否匹配");

                    // 检查 finish_reason，判断是否被截断
                    var finishReason = choices[0].TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                    if (finishReason == "length")
                    {
                        // 被 max_tokens 截断，记录警告
                        System.Diagnostics.Debug.WriteLine($"[AI Warning] 输出被截断，finish_reason=length，max_tokens={maxTokens}");
                    }

                    if (!choices[0].TryGetProperty("message", out var message))
                        throw new InvalidOperationException("AI 接口返回格式不兼容，未找到 choices[0].message");

                    var text = message.TryGetProperty("content", out var responseContent) && responseContent.ValueKind != JsonValueKind.Null
                        ? responseContent.GetString() ?? string.Empty
                        : string.Empty;
                    var toolCalls = new List<AICompletionToolCall>();
                    if (message.TryGetProperty("tool_calls", out var toolCallNodes) && toolCallNodes.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var toolCallNode in toolCallNodes.EnumerateArray())
                        {
                            if (!toolCallNode.TryGetProperty("function", out var functionNode)) continue;
                            var toolName = functionNode.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
                            if (string.IsNullOrWhiteSpace(toolName)) continue;
                            toolCalls.Add(new AICompletionToolCall
                            {
                                Id = toolCallNode.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? string.Empty : string.Empty,
                                Name = toolName,
                                ArgumentsJson = functionNode.TryGetProperty("arguments", out var argumentsNode)
                                    ? argumentsNode.GetString() ?? "{}"
                                    : "{}"
                            });
                        }
                    }

                    if (string.IsNullOrWhiteSpace(text) && toolCalls.Count == 0)
                    {
                        var snippet = resultJson.Length > 500 ? resultJson[..500] : resultJson;
                        System.Diagnostics.Debug.WriteLine(
                            $"[AI Warning] 第 {attempt}/{maxAttempts} 次请求收到空响应，finish_reason={finishReason ?? "null"}，原始片段：{snippet}");
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                            continue;
                        }
                        throw new InvalidOperationException(
                            "AI 接口连续 3 次返回空响应，请稍后重试；如多次出现，请检查 AI 模型配置或适当缩减会议内容长度");
                    }

                    // 如果被截断，只在末尾加一句简短的友好提示，不要覆盖正文
                    if (finishReason == "length")
                    {
                        text = text.TrimEnd();
                        if (text.Length > 0 && !text.EndsWith("（内容过长已自动精简）"))
                            text += "\n\n（内容过长已自动精简）";
                    }

                    int? inputTokens = null;
                    int? outputTokens = null;
                    if (result.RootElement.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var input)) inputTokens = input;
                        if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var output)) outputTokens = output;
                    }
                    decimal? estimatedCost = null;
                    if ((_inputPricePerMillion > 0 || _outputPricePerMillion > 0) && inputTokens.HasValue && outputTokens.HasValue)
                        estimatedCost = inputTokens.Value * _inputPricePerMillion / 1_000_000m
                            + outputTokens.Value * _outputPricePerMillion / 1_000_000m;

                    var responseModel = result.RootElement.TryGetProperty("model", out var modelNode)
                        ? modelNode.GetString()
                        : modelName;
                    return new AICompletionResult
                    {
                        Content = text,
                        ModelName = responseModel ?? modelName,
                        FinishReason = finishReason ?? string.Empty,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        EstimatedCost = estimatedCost,
                        ToolCalls = toolCalls
                    };
                }
            }

            // 理论上不可达：循环内要么返回要么抛错
            throw new InvalidOperationException("AI 接口连续 3 次返回空响应，请稍后重试");
        }
        #endregion

    }
}
