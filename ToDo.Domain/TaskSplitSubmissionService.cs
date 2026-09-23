using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain;

public sealed class TaskSplitSubmissionService(ApplicationDbContext context)
{
    public async Task<int> SaveAsync(int userId, Guid submissionId, IReadOnlyList<ToDoTask> tasks)
    {
        if (submissionId == Guid.Empty) throw new ArgumentException("缺少拆分批次，请重新拆分", nameof(submissionId));
        var key = $"{userId}:{submissionId:N}";
        var previous = await context.TaskSplitSubmissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
        if (previous != null) return previous.CreatedTaskCount;

        var submission = new TaskSplitSubmission { Id = key, CreatedTaskCount = tasks.Count };
        context.TaskSplitSubmissions.Add(submission);
        context.ToDoTasks.AddRange(tasks);
        try
        {
            // EF 在同一个事务中写入批次和任务；批次主键负责跨请求、跨进程去重。
            await context.SaveChangesAsync();
            return tasks.Count;
        }
        catch (DbUpdateException)
        {
            context.Entry(submission).State = EntityState.Detached;
            foreach (var task in tasks) context.Entry(task).State = EntityState.Detached;
            previous = await context.TaskSplitSubmissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == key);
            if (previous != null) return previous.CreatedTaskCount;
            throw;
        }
    }
}
