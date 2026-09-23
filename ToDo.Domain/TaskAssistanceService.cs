using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;
using TaskStatus = ToDo.Entities.TaskStatus;

namespace ToDo.Domain;

public sealed record TaskAssistancePurpose(string Key, string Label);

/// <summary>成员任务内的私有、只读协助，不改变正式任务及其执行者。</summary>
public sealed class TaskAssistanceService(ApplicationDbContext context, AgentRunQueueService runQueue)
{
    public const string AgentKey = "task-risk-review";
    public static IReadOnlyList<TaskAssistancePurpose> Purposes { get; } = Array.AsReadOnly(new[]
    {
        new TaskAssistancePurpose("next_steps", "梳理下一步"),
        new TaskAssistancePurpose("risk_check", "检查遗漏与风险"),
        new TaskAssistancePurpose("progress_update", "整理进展草稿")
    });

    public Task<bool> CanAssistAsync(int taskId, ApplicationUser user, CancellationToken ct = default)
        => CanAccessAsync(context, taskId, user.Id, true, ct);

    public async Task<AgentRunJob?> GetLatestAsync(int taskId, ApplicationUser user, CancellationToken ct = default)
    {
        if (!await CanAccessAsync(context, taskId, user.Id, false, ct)) return null;
        return await context.AgentRunJobs.AsNoTracking().Include(item => item.AiSession)
            .Where(item => item.TaskId == taskId && item.RequestedByUserId == user.Id
                && item.AiSession != null && item.AiSession.UserId == user.Id
                && context.ToDoTasks.Any(task => task.Id == taskId && task.ProjectId == item.AiSession.ProjectId)
                && item.AiSession.SessionKey.StartsWith(AgentSessionExecutionPolicy.AssistanceKeyPrefix)
                && item.AiSession.ArchivedAt == null)
            .OrderByDescending(item => item.Id).FirstOrDefaultAsync(ct);
    }

    public async Task<AgentRunJob> RequestAsync(int taskId, string purpose, ApplicationUser user, Guid requestId,
        CancellationToken ct = default)
    {
        if (!Purposes.Any(item => item.Key == purpose)) throw new InvalidOperationException("请选择有效的 AI 协助用途");
        if (requestId == Guid.Empty) throw new InvalidOperationException("请求标识已失效，请刷新任务后重试");
        if (!await CanAccessAsync(context, taskId, user.Id, true, ct))
            throw new UnauthorizedAccessException("当前任务不可请求协助，或你已不具备处理权限");
        var projectId = await context.ToDoTasks.AsNoTracking().Where(item => item.Id == taskId)
            .Select(item => item.ProjectId).SingleAsync(ct);
        var key = AgentSessionExecutionPolicy.AssistanceKeyPrefix + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{user.Id}:{taskId}:{requestId:N}"))).ToLowerInvariant();
        return await runQueue.EnqueueTaskAssistanceAsync(taskId, projectId, purpose, user, key, ct);
    }

    internal static async Task<bool> CanAccessAsync(ApplicationDbContext context, int taskId, int userId,
        bool requireActionable, CancellationToken ct = default)
    {
        // 不能相信请求缓存中的角色、旧创建者身份或已经撤销的项目关系。
        var user = await context.Users.AsNoTracking().FirstOrDefaultAsync(item => item.Id == userId
            && !item.IsDeleted && item.Status == UserStatus.Active, ct);
        if (user == null) return false;
        var query = context.ToDoTasks.AsNoTracking().Where(task => task.Id == taskId && !task.IsDeleted
            && task.Project != null && !task.Project.IsDeleted);
        if (requireActionable)
            query = query.Where(task => task.AssigneeType == TaskAssigneeType.Human && !task.IsCompleted
                && task.Status != TaskStatus.Completed && task.Status != TaskStatus.Cancelled);
        if (user.Role == UserRole.systemAdmin) return await query.AnyAsync(ct);
        return await query.AnyAsync(task => task.Project!.LeaderUserId == userId
            || context.ProjectUsers.Any(member => member.ProjectId == task.ProjectId && member.UserId == userId
                && (member.ProjectRole == 0 || task.AssigneeId == userId || task.ClaimerId == userId
                    || task.CreatorId == userId || task.ReviewerId == userId)), ct);
    }

    internal static async Task<bool> CanAccessSessionAsync(ApplicationDbContext context, AiSession session,
        int userId, bool requireActionable, CancellationToken ct = default)
        => session.UserId == userId && session.TaskId.HasValue && session.ProjectId.HasValue
            && await context.ToDoTasks.AsNoTracking().AnyAsync(task => task.Id == session.TaskId.Value
                && task.ProjectId == session.ProjectId.Value, ct)
            && await CanAccessAsync(context, session.TaskId.Value, userId, requireActionable, ct);

    internal static string BuildPrompt(string purpose) => purpose switch
    {
        "next_steps" => "请基于当前任务已有事实，给出我现在最值得做的 3 个下一步、每步应产出的成果和完成判断。列出真正阻塞执行的缺失信息，不重复任务全文，不编造已完成事项。",
        "risk_check" => "请检查当前任务的要求、评论及子任务，指出有事实依据的遗漏、依赖、风险和最小处理动作。区分已确认问题与待核实猜测；没有明确风险就直接说明，不制造工作。",
        "progress_update" => "请根据当前任务状态、进度及评论，整理一份可复制的简短进展草稿，分为已完成、正在做、阻塞和下一步。缺少证据的进展必须标为待补充，不能写成已完成；不要代我发布。",
        _ => throw new InvalidOperationException("请选择有效的 AI 协助用途")
    };
}
