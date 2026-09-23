using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public class AiSessionService
{
    private readonly ApplicationDbContext _context;

    public AiSessionService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<AiSession> StartAsync(string agentKey, string prompt, int? userId = null, int? projectId = null, int? taskId = null, string modelName = "", int agentVersion = 1, string? sessionKey = null, string? metadataJson = null)
    {
        var session = new AiSession
        {
            SessionKey = sessionKey ?? Guid.NewGuid().ToString("N"),
            MetadataJson = metadataJson ?? "{}",
            AgentKey = agentKey,
            Prompt = prompt,
            UserId = userId,
            ProjectId = projectId,
            TaskId = taskId,
            ModelName = modelName,
            AgentVersion = agentVersion,
            Status = AiSessionStatus.Running,
            LastActivityAt = AppTime.Now
        };
        _context.AiSessions.Add(session);
        await _context.SaveChangesAsync();
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = session.Id,
            Role = AiSessionMessageRole.User,
            Content = prompt
        });
        await _context.SaveChangesAsync();
        return session;
    }

    public async Task CompleteAsync(AiSession session, string response)
    {
        await CompleteTurnAsync(session, response);
    }

    public async Task BeginTurnAsync(AiSession session, string prompt)
    {
        if (session.Status == AiSessionStatus.Running) throw new InvalidOperationException("该 Session 正在执行");
        session.Status = AiSessionStatus.Running;
        session.ErrorMessage = string.Empty;
        session.CompletedAt = null;
        session.LastActivityAt = AppTime.Now;
        session.ConcurrencyVersion++;
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = session.Id,
            Role = AiSessionMessageRole.User,
            Content = prompt.Trim()
        });
        await _context.SaveChangesAsync();
    }

    public async Task CompleteTurnAsync(
        AiSession session,
        string response,
        int? inputTokens = null,
        int? outputTokens = null,
        long durationMilliseconds = 0,
        decimal? estimatedCost = null,
        string? modelName = null)
    {
        session.Response = response;
        session.Status = AiSessionStatus.WaitingHuman;
        session.TurnCount++;
        if (inputTokens.HasValue) session.InputTokens = (session.InputTokens ?? 0) + inputTokens.Value;
        if (outputTokens.HasValue) session.OutputTokens = (session.OutputTokens ?? 0) + outputTokens.Value;
        session.DurationMilliseconds += Math.Max(0, durationMilliseconds);
        if (estimatedCost.HasValue) session.EstimatedCost = (session.EstimatedCost ?? 0) + estimatedCost.Value;
        if (!string.IsNullOrWhiteSpace(modelName)) session.ModelName = modelName.Trim();
        session.LastActivityAt = AppTime.Now;
        session.ConcurrencyVersion++;
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = session.Id,
            Role = AiSessionMessageRole.Assistant,
            Content = response
        });
        await _context.SaveChangesAsync();
    }

    public async Task AddToolMessageAsync(AiSession session, string toolName, string content)
    {
        _context.AiSessionMessages.Add(new AiSessionMessage
        {
            AiSessionId = session.Id,
            Role = AiSessionMessageRole.Tool,
            ToolName = toolName,
            Content = content
        });
        session.LastActivityAt = AppTime.Now;
        session.ConcurrencyVersion++;
        await _context.SaveChangesAsync();
    }

    public async Task FailAsync(AiSession session, Exception exception)
    {
        session.Status = AiSessionStatus.Failed;
        session.ErrorMessage = exception.Message;
        session.CompletedAt = AppTime.Now;
        session.LastActivityAt = AppTime.Now;
        session.ConcurrencyVersion++;
        await _context.SaveChangesAsync();
    }

    public Task<List<AiSession>> GetRecentAsync(int take = 50, bool includeArchived = false)
    {
        var query = _context.AiSessions.AsNoTracking();
        if (!includeArchived) query = query.Where(item => item.ArchivedAt == null);
        return query.OrderByDescending(s => s.StartedAt).Take(take).ToListAsync();
    }

    public async Task<List<AiSession>> GetRecentAsync(ApplicationUser user, int take = 50, bool includeArchived = false)
    {
        var query = _context.AiSessions.AsNoTracking();
        if (user.Role != UserRole.systemAdmin) query = query.Where(item => item.UserId == user.Id);
        if (!includeArchived) query = query.Where(item => item.ArchivedAt == null);
        query = query.Where(item => !item.SessionKey.StartsWith(AgentSessionExecutionPolicy.AssistanceKeyPrefix)
            || item.UserId == user.Id);
        var limit = Math.Clamp(take, 1, 500);
        var visible = new List<AiSession>();
        DateTime? cursorTime = null;
        var cursorId = 0;
        // 限量针对可见结果，不能让前面的私有/已撤权会话挤掉后面的正常记录。
        // 用稳定游标分批读取，不固定放大 Take，也不一次载入全部历史。
        while (visible.Count < limit)
        {
            var pageQuery = query;
            if (cursorTime.HasValue)
                pageQuery = pageQuery.Where(item => item.LastActivityAt < cursorTime.Value
                    || (item.LastActivityAt == cursorTime.Value && item.Id < cursorId));
            var page = await pageQuery.OrderByDescending(item => item.LastActivityAt).ThenByDescending(item => item.Id)
                .Take(limit).ToListAsync();
            if (page.Count == 0) break;
            foreach (var item in page)
            {
                if (!AgentSessionExecutionPolicy.IsTaskAssistance(item)
                    || await TaskAssistanceService.CanAccessSessionAsync(_context, item, user.Id, false))
                    visible.Add(item);
                if (visible.Count == limit) break;
            }
            if (page.Count < limit) break;
            cursorTime = page[^1].LastActivityAt;
            cursorId = page[^1].Id;
        }
        return visible;
    }

    public async Task<AiSession?> GetDetailsAsync(int id, ApplicationUser user)
    {
        var session = await _context.AiSessions
            .Include(item => item.Messages.OrderBy(message => message.CreatedAt))
            .Include(item => item.ToolCalls.OrderBy(call => call.CreatedAt))
                .ThenInclude(call => call.ApprovalRequest)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (session == null) return null;
        if (AgentSessionExecutionPolicy.IsTaskAssistance(session))
            return await TaskAssistanceService.CanAccessSessionAsync(_context, session, user.Id, false) ? session : null;
        return user.Role == UserRole.systemAdmin || session.UserId == user.Id ? session : null;
    }

    public async Task CloseAsync(AiSession session)
    {
        MarkSucceeded(session);
        await _context.SaveChangesAsync();
    }

    /// <summary>
    /// 只更新 Session 完成状态，不立即提交。供需要把 Session 与业务状态
    /// 放在同一次 SaveChanges 中原子保存的调用方使用。
    /// </summary>
    public static void MarkSucceeded(AiSession session)
    {
        session.Status = AiSessionStatus.Succeeded;
        session.CompletedAt = AppTime.Now;
        session.LastActivityAt = AppTime.Now;
        session.ConcurrencyVersion++;
    }
}
