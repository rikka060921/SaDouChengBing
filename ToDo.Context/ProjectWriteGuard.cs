using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using System.Text.Json;
using ToDo.Entities;

namespace ToDo.Context;

/// <summary>
/// Last line of defence for tracked business writes (including Razor handlers).
/// Explicit allowlist: audit, notifications, AI transcripts and derived indexes remain writable.
/// Bulk SQL and filesystem writes must check ProjectLifecycleRules before their side effects.
/// </summary>
internal static class ProjectWriteGuard
{
    private static readonly HashSet<string> BusinessTypes = new(StringComparer.Ordinal)
    {
        nameof(ToDoTask), nameof(TaskGroup), nameof(TaskComment), nameof(TaskLabel), nameof(TaskLabelLink), nameof(DailyReport), nameof(DailyReportAttachment),
        nameof(ProjectDocument), nameof(MeetingMinutes), nameof(MeetingMinutesProject),
        nameof(MeetingVersion), nameof(MeetingAttachment), nameof(MeetingActionItem),
        nameof(MeetingAgenda), nameof(MeetingAgendaRelation), nameof(MeetingPrepDraft), nameof(DocumentCategory), nameof(AiDocumentDraft)
    };

    public static async Task ValidateAsync(ApplicationDbContext db, CancellationToken ct = default)
    {
        db.ChangeTracker.DetectChanges();
        var entries = db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        var projectIds = new HashSet<int>();
        var archiveIds = new HashSet<int>();
        var taskIds = new HashSet<int>();
        var meetingIds = new HashSet<int>();
        var reportIds = new HashSet<int>();
        var agendaIds = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.Entity is Project project)
            {
                if (entry.State != EntityState.Modified) continue;
                var before = entry.Property(nameof(Project.Status)).OriginalValue;
                if (entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not
                    (nameof(Project.Status) or nameof(Project.UpdatedAt) or nameof(Project.IsEncrypted)))
                    || (entry.Property(nameof(Project.IsEncrypted)).IsModified &&
                        project.IsEncrypted < (char)entry.Property(nameof(Project.IsEncrypted)).OriginalValue!))
                    projectIds.Add(project.Id);
                if (Equals(before, ProjectStatus.Archived))
                {
                    // Restoring and revoking visibility are administrative actions, not business work.
                    if (entry.Properties.Any(p => p.IsModified && p.Metadata.Name is not
                        (nameof(Project.Status) or nameof(Project.UpdatedAt) or nameof(Project.IsEncrypted))))
                        throw new InvalidOperationException(ProjectLifecycleRules.ReadOnlyMessage);
                    if (entry.Property(nameof(Project.IsEncrypted)).IsModified &&
                        project.IsEncrypted < (char)entry.Property(nameof(Project.IsEncrypted)).OriginalValue!)
                        throw new InvalidOperationException(ProjectLifecycleRules.ReadOnlyMessage);
                }
                if (Equals(before, ProjectStatus.Active) && project.Status == ProjectStatus.Archived)
                {
                    archiveIds.Add(project.Id);
                }
                continue;
            }
            if (entry.Entity is ProjectUser member)
            {
                // Revocation/demotion is still available to administrators.
                if (entry.State == EntityState.Deleted) continue;
                if (entry.State == EntityState.Modified && entry.Properties.Where(p => p.IsModified)
                    .All(p => p.Metadata.Name is nameof(ProjectUser.ProjectRole) or nameof(ProjectUser.IsProjectAdmin)) &&
                    member.ProjectRole >= Convert.ToInt32(entry.Property(nameof(ProjectUser.ProjectRole)).OriginalValue) &&
                    (!member.IsProjectAdmin || Equals(entry.Property(nameof(ProjectUser.IsProjectAdmin)).OriginalValue, true))) continue;
            }
            else if (entry.Entity is AgentWorkItem work)
            {
                if (work.Status is not (AgentWorkItemStatus.Pending or AgentWorkItemStatus.Running or AgentWorkItemStatus.Retrying or AgentWorkItemStatus.WaitingPlanConfirmation)) continue;
            }
            else if (entry.Entity is AgentRunJob run)
            {
                if (run.Status is not (AgentRunJobStatus.Pending or AgentRunJobStatus.Running or AgentRunJobStatus.Retrying)) continue;
            }
            else if (entry.Entity is AgentEventExecution execution)
            {
                if (execution.Status is not (AgentEventExecutionStatus.Pending or AgentEventExecutionStatus.Running or AgentEventExecutionStatus.Retrying)) continue;
            }
            else if (entry.Entity is ApprovalRequest approval)
            {
                if (entry.State != EntityState.Added || approval.Status != ApprovalRequestStatus.Pending) continue;
            }
            else if (entry.Entity is AgentEventSubscription subscription)
            {
                if (entry.State == EntityState.Deleted || (entry.State == EntityState.Modified && !subscription.IsEnabled &&
                    entry.Properties.Where(p => p.IsModified).All(p => p.Metadata.Name is "IsEnabled" or "UpdatedAt"))) continue;
            }
            else if (entry.Entity is ScheduledJob job)
            {
                if (entry.State == EntityState.Deleted) continue;
                if (entry.State == EntityState.Modified && !entry.Properties.Any(p => p.IsModified &&
                    (p.Metadata.Name is "ProjectId" or "UserId" or "JobType" or "RunAt" or "JobKey" or "Name" ||
                     p.Metadata.Name == "IsEnabled" && job.IsEnabled))) continue;
            }
            else if (!BusinessTypes.Contains(entry.Metadata.ClrType.Name)) continue;

            if (entry.Entity is ProjectDocument && entry.State == EntityState.Modified &&
                entry.Properties.Where(p => p.IsModified).All(p => p.Metadata.Name.StartsWith("Index", StringComparison.Ordinal) || p.Metadata.Name == "ExtractedCharacterCount")) continue;

            AddIds(entry, "ProjectId", projectIds);
            AddIds(entry, "TaskId", taskIds);
            AddIds(entry, "MeetingMinutesId", meetingIds);
            AddIds(entry, "DailyReportId", reportIds);
            AddIds(entry, "MeetingAgendaId", agendaIds);
            if (entry.Entity is MeetingMinutes meeting && meeting.Id > 0) meetingIds.Add(meeting.Id);
            if (entry.Entity is MeetingPrepDraft)
            {
                AddJsonIds(entry, nameof(MeetingPrepDraft.SelectedProjectIdsJson), projectIds);
                AddJsonIds(entry, nameof(MeetingPrepDraft.SelectedTaskIdsJson), taskIds);
            }
        }
        if (taskIds.Count > 0)
            projectIds.UnionWith(await db.ToDoTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id)).Select(t => t.ProjectId).ToListAsync(ct));
        if (reportIds.Count > 0)
            projectIds.UnionWith(await db.DailyReport.AsNoTracking().Where(r => reportIds.Contains(r.Id)).Select(r => r.ProjectId).ToListAsync(ct));
        if (agendaIds.Count > 0)
            projectIds.UnionWith(await db.MeetingAgendas.AsNoTracking().Where(a => agendaIds.Contains(a.Id)).Select(a => a.ProjectId).ToListAsync(ct));
        if (meetingIds.Count > 0)
        {
            projectIds.UnionWith(await db.MeetingMinutes.AsNoTracking().Where(m => meetingIds.Contains(m.Id)).Select(m => m.ProjectId).ToListAsync(ct));
            projectIds.UnionWith(await db.MeetingMinutesProjects.AsNoTracking().Where(m => meetingIds.Contains(m.MeetingMinutesId)).Select(m => m.ProjectId).ToListAsync(ct));
        }
        var lockIds = projectIds.Concat(entries.Where(e => e.Entity is Project).Select(e => ((Project)e.Entity).Id)).Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        var lockedInactive = await LockProjectsAsync(db, lockIds, ct);
        foreach (var projectId in archiveIds)
        {
            var check = await ProjectLifecycleRules.CheckArchiveAsync(db, projectId, ct);
            if (!check.CanArchive) throw new InvalidOperationException(string.Join("\n", check.Blockers));
        }
        if (projectIds.Count == 0) return;
        // A locking read must be used on MySQL: a repeatable-read snapshot may predate the archive.
        if (lockedInactive.Overlaps(projectIds) || entries.Any(e => e.Entity is Project p && projectIds.Contains(p.Id) && p.Status == ProjectStatus.Archived)
            || await db.Project.AsNoTracking().AnyAsync(p => projectIds.Contains(p.Id) && (p.Status == ProjectStatus.Archived || p.IsDeleted), ct))
            throw new InvalidOperationException(ProjectLifecycleRules.ReadOnlyMessage);
    }

    private static async Task<HashSet<int>> LockProjectsAsync(ApplicationDbContext db, int[] ids, CancellationToken ct)
    {
        var inactive = new HashSet<int>();
        if (ids.Length == 0 || db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true)
            return inactive;
        var table = db.Model.FindEntityType(typeof(Project))!.GetTableName()!.Replace("`", "``");
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        var names = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@archiveProject" + i;
            parameter.Value = ids[i];
            command.Parameters.Add(parameter);
            names.Add(parameter.ParameterName);
        }
        command.CommandText = $"SELECT Id, Status, IsDeleted FROM `{table}` WHERE Id IN ({string.Join(",", names)}) ORDER BY Id FOR UPDATE";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (Convert.ToInt32(reader.GetValue(1)) != (int)ProjectStatus.Active || Convert.ToBoolean(reader.GetValue(2)))
                inactive.Add(Convert.ToInt32(reader.GetValue(0)));
        return inactive;
    }

    private static void AddIds(EntityEntry entry, string name, HashSet<int> ids)
    {
        if (entry.Metadata.FindProperty(name) == null) return;
        var property = entry.Property(name);
        if (property.CurrentValue is int current && current > 0) ids.Add(current);
        if (entry.State != EntityState.Added && property.OriginalValue is int original && original > 0) ids.Add(original);
    }

    private static void AddJsonIds(EntityEntry entry, string name, HashSet<int> ids)
    {
        var property = entry.Property(name);
        ids.UnionWith(JsonSerializer.Deserialize<int[]>((string?)property.CurrentValue ?? "[]") ?? []);
        if (entry.State != EntityState.Added)
            ids.UnionWith(JsonSerializer.Deserialize<int[]>((string?)property.OriginalValue ?? "[]") ?? []);
    }
}
