using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class RedBlueService
{
    private readonly ApplicationDbContext _context;
    private readonly AgentExecutionService _agents;
    private readonly IEventBus _eventBus;
    private readonly AgentDocumentAccessService _documentAccess;

    public RedBlueService(ApplicationDbContext context, AgentExecutionService agents, IEventBus eventBus, AgentDocumentAccessService documentAccess)
    {
        _context = context;
        _agents = agents;
        _eventBus = eventBus;
        _documentAccess = documentAccess;
    }

    public async Task<List<RedBlueSession>> GetRecentAsync(int? projectId = null, CancellationToken cancellationToken = default)
    {
        var query = _context.RedBlueSessions.AsNoTracking().OrderByDescending(s => s.CreatedAt).AsQueryable();
        if (projectId.HasValue) query = query.Where(s => s.ProjectId == projectId.Value);
        return await query.Take(50).ToListAsync(cancellationToken);
    }

    public Task<RedBlueSession?> GetDetailsAsync(int id, CancellationToken cancellationToken = default)
    {
        return _context.RedBlueSessions.Include(s => s.Rounds.OrderBy(r => r.RoundNumber)).FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<RedBlueSession> CreateAsync(string topic, string objective, int maxRounds, int createdById, int? projectId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("主题不能为空", nameof(topic));
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("决策目标不能为空", nameof(objective));

        var session = new RedBlueSession
        {
            Topic = topic.Trim(),
            Objective = objective.Trim(),
        MaxRounds = Math.Clamp(maxRounds, 1, 5),
            CreatedById = createdById,
            ProjectId = projectId,
            Status = RedBlueSessionStatus.Draft
        };
        _context.RedBlueSessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task RunAsync(RedBlueSession session, CancellationToken cancellationToken = default)
    {
        if (session.Status == RedBlueSessionStatus.Running) throw new InvalidOperationException("该对抗正在运行");

        session.Status = RedBlueSessionStatus.Running;
        session.CurrentRound = 0;
        session.Winner = string.Empty;
        session.FinalDecision = string.Empty;
        var oldRounds = await _context.RedBlueRounds.Where(r => r.RedBlueSessionId == session.Id).ToListAsync(cancellationToken);
        if (oldRounds.Count > 0) _context.RedBlueRounds.RemoveRange(oldRounds);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            var projectContext = await BuildProjectContextAsync(session.ProjectId, session.CreatedById, cancellationToken);
            var previousDecision = "暂无上一轮结论";
            for (var roundNumber = 1; roundNumber <= session.MaxRounds; roundNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var redPrompt = $"你是红方策略 Agent。围绕主题“{session.Topic}”和决策目标“{session.Objective}”，提出可执行、进攻性强且有证据依据的方案。第 {roundNumber} 轮。上一轮结论：{previousDecision}\n当前项目上下文：\n{projectContext}";
                var (_, red) = await _agents.RunAsync("red-team", redPrompt, session.CreatedById, session.ProjectId);

                var bluePrompt = $"你是蓝方防守 Agent。围绕主题“{session.Topic}”和决策目标“{session.Objective}”，结合当前项目任务与资料审查红方方案，指出风险、反例和落地改进。\n当前项目上下文：\n{projectContext}\n红方方案：{red}";
                var (_, blue) = await _agents.RunAsync("blue-team", bluePrompt, session.CreatedById, session.ProjectId);

                var judgePrompt = $$"""
                    你是红蓝对抗裁判。比较双方方案，输出关键依据、风险控制和下一轮建议。
                    正常裁判意见末尾必须另起一行输出且只输出一个结构化结论：
                    {{RedBlueJudgeParser.ResultStart}}{"winner":"red|blue|draw"}{{RedBlueJudgeParser.ResultEnd}}
                    winner 只能选择 red、blue、draw 之一。
                    主题：{{session.Topic}}
                    红方：{{red}}
                    蓝方：{{blue}}
                    """;
                var (_, rawDecision) = await _agents.RunAsync("judge", judgePrompt, session.CreatedById, session.ProjectId);
                var judgeResult = RedBlueJudgeParser.Parse(rawDecision);
                var decision = judgeResult.Decision;

                _context.RedBlueRounds.Add(new RedBlueRound
                {
                    RedBlueSessionId = session.Id,
                    RoundNumber = roundNumber,
                    RedArgument = red,
                    BlueArgument = blue,
                    Winner = judgeResult.Winner,
                    JudgeDecision = decision
                });
                session.CurrentRound = roundNumber;
                previousDecision = decision;
                await _context.SaveChangesAsync(cancellationToken);
            }

            var rounds = await _context.RedBlueRounds.Where(r => r.RedBlueSessionId == session.Id).ToListAsync(cancellationToken);
            session.Winner = RedBlueJudgeParser.ResolveOverallWinner(rounds.Select(round => round.Winner));
            session.FinalDecision = rounds.OrderByDescending(r => r.RoundNumber).FirstOrDefault()?.JudgeDecision ?? string.Empty;
            session.Status = RedBlueSessionStatus.Completed;
            session.CompletedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
            await _eventBus.PublishAsync("red-blue.completed", new { session.Id, session.Winner }, "RedBlueSession", session.Id.ToString(), cancellationToken: cancellationToken);
        }
        catch
        {
            session.Status = RedBlueSessionStatus.Failed;
            session.CompletedAt = AppTime.Now;
            await _context.SaveChangesAsync(cancellationToken);
            await _eventBus.PublishAsync("red-blue.failed", new { session.Id }, "RedBlueSession", session.Id.ToString(), cancellationToken: cancellationToken);
            throw;
        }
    }

    public async Task<List<Project>> GetAccessibleProjectsAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var query = _context.Project.AsNoTracking().Where(item => !item.IsDeleted);
        if (user.Role != UserRole.systemAdmin)
        {
            query = query.Where(project => project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id));
        }
        return await query.OrderBy(item => item.Name).ToListAsync(cancellationToken);
    }

    public async Task<bool> CanAccessProjectAsync(int projectId, ApplicationUser user, CancellationToken cancellationToken = default)
    {
        if (user.Role == UserRole.systemAdmin) return true;
        return await _context.Project.AsNoTracking().AnyAsync(project => project.Id == projectId
            && !project.IsDeleted
            && (project.LeaderUserId == user.Id
                || _context.ProjectUsers.Any(member => member.ProjectId == project.Id && member.UserId == user.Id)), cancellationToken);
    }

    public async Task<bool> CanAccessSessionAsync(RedBlueSession session, ApplicationUser user, CancellationToken cancellationToken = default)
    {
        return !session.ProjectId.HasValue || await CanAccessProjectAsync(session.ProjectId.Value, user, cancellationToken);
    }

    public async Task<RedBlueSetupSuggestion?> SuggestSetupAsync(int projectId, ApplicationUser user, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessProjectAsync(projectId, user, cancellationToken)) return null;
        var project = await _context.Project.AsNoTracking().FirstAsync(item => item.Id == projectId, cancellationToken);
        var openTasks = await _context.ToDoTasks.AsNoTracking().CountAsync(item => item.ProjectId == projectId
            && !item.IsDeleted && !item.IsCompleted
            && item.Status != ToDo.Entities.TaskStatus.Completed
            && item.Status != ToDo.Entities.TaskStatus.Cancelled, cancellationToken);
        var overdueTasks = await _context.ToDoTasks.AsNoTracking().CountAsync(item => item.ProjectId == projectId
            && !item.IsDeleted && !item.IsCompleted
            && item.Status != ToDo.Entities.TaskStatus.Completed
            && item.Status != ToDo.Entities.TaskStatus.Cancelled
            && item.EndTime.HasValue && item.EndTime.Value < AppTime.Now, cancellationToken);
        var documents = await _context.ProjectDocuments.AsNoTracking().CountAsync(item => item.ProjectId == projectId && item.IsCurrent, cancellationToken);
        return new RedBlueSetupSuggestion
        {
            Topic = $"{project.Name} 当前计划与风险评审",
            Objective = $"结合 {openTasks} 项未完成任务（其中 {overdueTasks} 项逾期）和 {documents} 份当前项目资料，形成可执行决策并明确风险与后续行动。",
            MaxRounds = 3
        };
    }

    private async Task<string> BuildProjectContextAsync(int? projectId, int userId, CancellationToken cancellationToken)
    {
        if (!projectId.HasValue) return "未关联项目";
        var project = await _context.Project.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId.Value && !item.IsDeleted, cancellationToken);
        if (project == null) return "项目不存在或已删除";

        var tasks = await _context.ToDoTasks.AsNoTracking()
            .Where(item => item.ProjectId == projectId.Value && !item.IsDeleted)
            .OrderBy(item => item.Status == ToDo.Entities.TaskStatus.Completed)
            .ThenBy(item => item.EndTime)
            .Take(80)
            .Select(item => new { item.Title, item.Status, item.Priority, item.Progress, item.EndTime, item.AgentName, item.AssigneeType })
            .ToListAsync(cancellationToken);
        var documents = await _documentAccess.ReadDocumentsAsync(
            projectId.Value,
            "red-blue-host",
            userId,
            maxDocuments: 20,
            maxCharactersPerDocument: 2000,
            cancellationToken: cancellationToken);

        var taskLines = tasks.Count == 0
            ? "- 暂无任务"
            : string.Join("\n", tasks.Select(item => $"- {item.Title}｜状态 {item.Status}｜优先级 {item.Priority}｜进度 {item.Progress}%｜截止 {(item.EndTime?.ToString("yyyy-MM-dd") ?? "未设置")}｜执行主体 {(item.AssigneeType == TaskAssigneeType.DigitalEmployee ? item.AgentName ?? "Agent" : "人员")}"));
        var documentLines = documents.Count == 0
            ? "- 暂无已授权项目资料"
            : string.Join("\n\n", documents.Select(item => $"- {item.FileName} v{item.VersionNumber}｜{item.Category.GetDisplayName()}｜{(string.IsNullOrWhiteSpace(item.Description) ? "无说明" : item.Description)}\n{item.ContentExcerpt}"));
        return $"项目：{project.Name}\n项目目标与说明：{project.Description}\n任务：\n{taskLines}\n已授权项目资料：\n{documentLines}";
    }

}

public sealed class RedBlueSetupSuggestion
{
    public string Topic { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public int MaxRounds { get; init; } = 3;
}
