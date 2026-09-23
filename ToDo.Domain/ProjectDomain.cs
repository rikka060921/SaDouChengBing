using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Text.Json;
using ToDo.Context;
using ToDo.Entities;
using ToDo.Entities.Dto;

namespace ToDo.Domain
{
    public class ProjectDomain
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ProjectDomain> _logger;

        public ProjectDomain(ApplicationDbContext context, ILogger<ProjectDomain> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// SQL字符串转义辅助方法
        /// </summary>
        private string EscapeSqlString(string input)
        {
            if (IsInMemoryDatabase() || string.IsNullOrEmpty(input))
            {
                return input ?? string.Empty;
            }

            // 在生产环境中使用MySqlHelper，在测试环境中简单处理
            try
            {
                return MySqlHelper.EscapeString(input);
            }
            catch
            {
                // 如果MySqlHelper不可用，进行基本转义
                return input.Replace("'", "''");
            }
        }

        /// <summary>
        /// 检查是否使用内存数据库
        /// </summary>
        private bool IsInMemoryDatabase()
        {
            return _context.Database.ProviderName?.Contains("InMemory") == true ||
                   _context.Database.IsInMemory();
        }

        /// <summary>
        /// 安全地执行SQL命令（只在关系数据库中使用）
        /// </summary>
        private async Task ExecuteSqlSafelyAsync(FormattableString sql)
        {
            if (!IsInMemoryDatabase())
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(sql);
            }
            // 内存数据库中忽略SQL命令
        }

        /// <summary>
        /// 安全地执行SQL命令（字符串版本）
        /// </summary>
        private async Task ExecuteSqlSafelyAsync(string sql, params object[] parameters)
        {
            if (!IsInMemoryDatabase())
            {
                if (parameters != null && parameters.Length > 0)
                {
                    await _context.Database.ExecuteSqlRawAsync(sql, parameters);
                }
                else
                {
                    await _context.Database.ExecuteSqlRawAsync(sql);
                }
            }
            // 内存数据库中忽略SQL命令
        }

        /// <summary>
        /// 安全地开始事务
        /// </summary>
        private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionSafelyAsync(bool serializable = false)
        {
            if (IsInMemoryDatabase())
            {
                // 返回一个空的事务对象
                return new NullDbContextTransaction();
            }
            return serializable
                ? await _context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)
                : await _context.Database.BeginTransactionAsync();
        }

        // 空事务实现
        private class NullDbContextTransaction : Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
        {
            public Guid TransactionId => Guid.NewGuid();
            public void Commit() { }
            public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Rollback() { }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        /// <summary>
        /// 安全地设置MySQL会话变量
        /// </summary>
        private async Task SetMySqlSessionVariablesAsync(int userId, string userName, string realName)
        {
            if (_context.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            // 使用参数化查询来避免SQL注入和转义问题
            var sql = @"
        SET @current_user_id = @userId;
        SET @current_user_name = @userName;
        SET @current_real_name = @realName;";

            var parameters = new[]
            {
                new MySqlParameter("@userId", userId),
                new MySqlParameter("@userName", userName ?? "system"),
                new MySqlParameter("@realName", realName ?? "system")
            };

            await ExecuteSqlSafelyAsync(sql, parameters);
        }

        /// <summary>
        /// 创建项目（完善版：增强事务一致性、异常处理和日志记录）
        /// </summary>
        public async Task<int> CreateProjectAsync(
            string projectName,
            string description,
            int currentUserId,
            string? requirements = null,
            char isEncrypted = '0')
        {
            // 输入参数校验
            if (string.IsNullOrWhiteSpace(projectName))
            {
                _logger.LogError("用户 {UserId} 创建项目失败：项目名称为空", currentUserId);
                throw new ArgumentException("项目名称不能为空", nameof(projectName));
            }

            if (projectName.Length > 255)
            {
                _logger.LogError("用户 {UserId} 创建项目失败：名称长度超过255字符（实际：{Length}）",
                    currentUserId, projectName.Length);
                throw new ArgumentException("项目名称长度不能超过255字符", nameof(projectName));
            }

            if (!new[] { '0', '1', '2' }.Contains(isEncrypted))
            {
                _logger.LogError("用户 {UserId} 创建项目失败：无效加密状态 '{Encrypted}'",
                    currentUserId, isEncrypted);
                throw new ArgumentException("加密状态只能是0、1或2", nameof(isEncrypted));
            }

            // 使用安全的事务
            using var transaction = await BeginTransactionSafelyAsync();
            try
            {
                // 获取当前用户信息
                var currentUser = await _context.Users
                    .Where(u => u.Id == currentUserId)
                    .Select(u => new { u.UserName, u.RealName })
                    .FirstOrDefaultAsync();

                if (currentUser == null)
                {
                    throw new ArgumentException("当前用户不存在");
                }

                // 安全地设置MySQL会话变量（使用参数化查询）
                await SetMySqlSessionVariablesAsync(
                    currentUserId,
                    currentUser.UserName ?? "system",
                    currentUser.RealName ?? "system");

                // 创建项目实体
                var project = new Project
                {
                    Name = projectName.Trim(),
                    Description = description?.Trim() ?? string.Empty,
                    Requirements = requirements?.Trim() ?? string.Empty,
                    CreatedByUserId = currentUserId,
                    LeaderUserId = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    IsDeleted = false,
                    Status = ProjectStatus.Active,
                    IsEncrypted = isEncrypted
                };

                _context.Project.Add(project);
                await _context.SaveChangesAsync();

                // 显式添加创建者为管理员
                _context.ProjectUsers.Add(new ProjectUser
                {
                    ProjectId = project.Id,
                    UserId = currentUserId,
                    ProjectRole = (int)ProjectRole.Admin,
                    IsCreator = true,
                    IsProjectAdmin = true,
                });

                await _context.SaveChangesAsync();

                // 记录项目创建日志
                await LogProjectOperationAsync(
                    OperationType.创建,
                    project.Id,
                    currentUserId,
                    beforeState: "无项目",
                    afterState: $"创建项目: {project.Name}, " +
                               $"加密状态: {GetEncryptionDisplayName(project.IsEncrypted)}, " +
                               $"负责人: {currentUser.RealName}, " +
                               $"创建时间: {AppTime.ToBeijingTime(project.CreatedAt):yyyy-MM-dd HH:mm:ss}"
                );

                if (!IsInMemoryDatabase())
                {
                    await transaction.CommitAsync();
                }

                _logger.LogInformation("用户 {UserId} 成功创建项目 {ProjectName} (ID: {ProjectId})",
                    currentUserId, project.Name, project.Id);
                return project.Id;
            }
            catch (Exception ex)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }

                // 记录创建失败日志
                await LogProjectOperationAsync(
                    OperationType.创建,
                    0,
                    currentUserId,
                    beforeState: $"尝试创建项目: {projectName}",
                    afterState: $"创建失败: {ex.Message}",
                    status: OperationStatus.失败
                );

                _logger.LogError(ex, "用户 {UserId} 创建项目 {ProjectName} 失败: {ErrorMessage}",
                    currentUserId, projectName, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 获取加密状态的中文显示名称
        /// </summary>
        private string GetEncryptionDisplayName(char isEncrypted)
        {
            return isEncrypted switch
            {
                '0' => "公开",
                '1' => "成员可见",
                '2' => "仅负责人可见",
                _ => "未知状态"
            };
        }

        public async Task<int> GetUnfinishedTaskCountAsync(int projectId)
        {
            return await _context.ToDoTasks
                .CountAsync(t => t.ProjectId == projectId &&
                               !t.IsCompleted &&
                               !t.IsDeleted);
        }

        public async Task<(List<ProjectListDto> Projects, int TotalCount)> GetProjectsAsync(
            int currentUserId,
            UserRole currentUserRole,
            int pageIndex = 1,
            int pageSize = 10,
            string? keyword = null,
            bool? isArchivedFilter = false)
        {
            pageIndex = Math.Max(1, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = _context.Project
                .AsNoTracking()
                .Include(p => p.CreatedByUser)
                .Include(p => p.LeaderUser)
                .Include(p => p.ProjectUsers)
                .Where(p => !p.IsDeleted);

            // 权限过滤
            if (currentUserRole != UserRole.systemAdmin)
            {
                query = query.Where(p =>
                    p.IsEncrypted == '0' ||
                    (p.IsEncrypted == '1' && (p.LeaderUserId == currentUserId || p.ProjectUsers.Any(pu => pu.UserId == currentUserId))) ||
                    (p.IsEncrypted == '2' && p.LeaderUserId == currentUserId)
                );
            }

            // 状态过滤
            if (isArchivedFilter.HasValue)
            {
                query = query.Where(p => p.Status ==
                    (isArchivedFilter.Value ? ProjectStatus.Archived : ProjectStatus.Active));
            }

            // 关键词搜索
            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(p =>
                    EF.Functions.Like(p.Name, $"%{keyword}%") ||
                    EF.Functions.Like(p.Description ?? string.Empty, $"%{keyword}%"));
            }

            var totalCount = await query.CountAsync();
            _logger.LogDebug("用户 {UserId}（角色：{Role}）查询项目列表：总数 {TotalCount}，页码 {PageIndex}",
                currentUserId, currentUserRole, totalCount, pageIndex);

            var projects = await query
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Skip((int)Math.Min((long)(pageIndex - 1) * pageSize, int.MaxValue))
                .Take(pageSize)
                .Select(p => new ProjectListDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description ?? string.Empty,
                    CreatedAt = p.CreatedAt,
                    CreatedByUserName = p.CreatedByUser.UserName ?? string.Empty,
                    CreatedByUserId = p.CreatedByUserId,
                    Requirements = p.Requirements,
                    Status = p.Status,
                    IsEncrypted = p.IsEncrypted,
                    LeaderUserId = p.LeaderUserId,
                    LeaderUserName = p.LeaderUser.RealName ?? p.LeaderUser.UserName ?? string.Empty,
                    IsCurrentUserSystemAdmin = currentUserRole == UserRole.systemAdmin,
                    IsCurrentUserLeader = p.LeaderUserId == currentUserId,
                    IsCurrentUserProjectAdmin = p.ProjectUsers.Any(pu =>
                        pu.UserId == currentUserId &&
                        pu.ProjectRole == (int)ProjectRole.Admin),
                    IsCurrentUserMember = p.ProjectUsers.Any(pu => pu.UserId == currentUserId)
                })
                .ToListAsync();

            return (projects, totalCount);
        }

        /// <summary>
        /// Uses the same visibility rules as the project list so a direct details URL
        /// cannot bypass an encrypted project's access boundary.
        /// </summary>
        public Task<bool> CanViewProjectAsync(int projectId, int currentUserId, UserRole currentUserRole)
        {
            var query = _context.Project
                .AsNoTracking()
                .Where(project => project.Id == projectId && !project.IsDeleted);

            if (currentUserRole == UserRole.systemAdmin)
                return query.AnyAsync();

            return query.AnyAsync(project =>
                project.IsEncrypted == '0'
                || (project.IsEncrypted == '1'
                    && (project.LeaderUserId == currentUserId
                        || project.ProjectUsers.Any(member => member.UserId == currentUserId)))
                || (project.IsEncrypted == '2' && project.LeaderUserId == currentUserId));
        }

        public async Task<bool> ProjectNameExistsAsync(string projectName, int userId)
        {
            return await _context.Project
                .AnyAsync(p => p.Name == projectName && p.CreatedByUserId == userId && !p.IsDeleted);
        }

        /// <summary>
        /// 项目归档/恢复（含完整的日志记录）
        /// </summary>
        public async Task<bool> SetArchiveStatusAsync(
            int projectId,
            bool shouldArchive,
            int currentUserId,
            UserRole currentUserRole,
            bool confirmUnfinishedTasks = false)
        {
            using var transaction = await BeginTransactionSafelyAsync(serializable: true);
            try
            {
                // 1. 获取项目和操作人信息
                var project = await _context.Project
                    .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

                var operatorUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == currentUserId);

                if (project == null || operatorUser == null)
                {
                    _logger.LogWarning("项目 {ProjectId} 或用户 {UserId} 不存在", projectId, currentUserId);
                    return false;
                }

                // 2. 权限验证
                if (!await CanManageProjectAsync(projectId, currentUserId, currentUserRole))
                {
                    _logger.LogWarning("用户 {UserId} 无权限对项目 {ProjectId} 执行归档/恢复操作",
                        currentUserId, projectId);
                    return false;
                }

                // 3. 检查状态是否已为目标状态
                var targetStatus = shouldArchive ? ProjectStatus.Archived : ProjectStatus.Active;
                if (project.Status == targetStatus)
                {
                    _logger.LogInformation("项目 {ProjectId} 状态已是 {Status}，无需操作",
                        projectId, targetStatus);
                    return true;
                }

                if (shouldArchive)
                {
                    var check = await ProjectLifecycleRules.CheckArchiveAsync(_context, projectId);
                    if (!check.CanArchive) throw new InvalidOperationException(string.Join("\n", check.Blockers));
                    if (check.UnfinishedTasks > 0 && !confirmUnfinishedTasks)
                        throw new InvalidOperationException($"还有 {check.UnfinishedTasks} 项未完成任务。请确认暂停推进；任务状态与进度不会改变。");
                }
                else
                {
                    await ResetAutomationWatermarkAsync(projectId);
                }

                // 4. 安全地设置MySQL会话变量（使用参数化查询）
                await SetMySqlSessionVariablesAsync(
                    currentUserId,
                    operatorUser.UserName ?? string.Empty,
                    operatorUser.RealName ?? "");

                // 5. 记录操作前状态
                var beforeState = $"项目状态: {GetStatusDisplayName(project.Status)}";

                // 6. 更新项目状态
                var originalStatus = project.Status;
                project.Status = targetStatus;
                project.UpdatedAt = DateTime.UtcNow;

                // 7. 记录操作日志
                await LogProjectOperationAsync(
                    operationType: OperationType.更新,
                    projectId: projectId,
                    operatorUserId: currentUserId,
                    beforeState: beforeState,
                    afterState: $"项目状态: {GetStatusDisplayName(targetStatus)}",
                    status: OperationStatus.成功
                );

                await _context.SaveChangesAsync();

                if (!IsInMemoryDatabase())
                {
                    await transaction.CommitAsync();
                }

                _logger.LogInformation("用户 {UserId} 成功{Action}项目 {ProjectId}（{OriginalStatus} → {TargetStatus}）",
                    currentUserId, shouldArchive ? "归档" : "恢复", projectId, originalStatus, targetStatus);
                return true;
            }
            catch (Exception ex)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }

                if (ex is InvalidOperationException) throw;
                _logger.LogError(ex, "{Action}项目 {ProjectId} 失败",
                    shouldArchive ? "归档" : "恢复", projectId);
                return false;
            }
        }

        /// <summary>
        /// 恢复只开启后续业务，旧工作与归档期间的事件不会自动补跑。
        /// </summary>
        private async Task ResetAutomationWatermarkAsync(int projectId)
        {
            var now = AppTime.Now;
            var checkpoint = await _context.ProjectSummaryCheckpoints.SingleOrDefaultAsync(c => c.ProjectId == projectId);
            if (checkpoint == null)
            {
                checkpoint = new ProjectSummaryCheckpoint { ProjectId = projectId };
                _context.ProjectSummaryCheckpoints.Add(checkpoint);
            }
            // Only legacy/inconsistent archived data can contain unfinished jobs: do not revive it on restore.
            foreach (var work in await _context.AgentWorkItems.Where(w => w.ProjectId == projectId &&
                w.Status != AgentWorkItemStatus.Completed && w.Status != AgentWorkItemStatus.Failed && w.Status != AgentWorkItemStatus.Cancelled).ToListAsync())
            {
                work.Status = AgentWorkItemStatus.Cancelled; work.LockedAt = null; work.CompletedAt = now;
                work.ErrorMessage = "项目恢复时保留历史，不自动重启旧工作；如需继续请重新委托。"; work.UpdatedAt = now;
            }
            foreach (var run in await _context.AgentRunJobs.Where(w => w.ProjectId == projectId &&
                w.Status != AgentRunJobStatus.Completed && w.Status != AgentRunJobStatus.Failed && w.Status != AgentRunJobStatus.Cancelled).ToListAsync())
            {
                run.Status = AgentRunJobStatus.Cancelled; run.LockedAt = null; run.CompletedAt = now;
                run.ErrorMessage = "项目恢复不自动重启旧运行。"; run.UpdatedAt = now;
            }
            foreach (var execution in await _context.AgentEventExecutions.Where(w => w.ProjectId == projectId &&
                (w.Status == AgentEventExecutionStatus.Pending || w.Status == AgentEventExecutionStatus.Running ||
                 w.Status == AgentEventExecutionStatus.Retrying || w.Status == AgentEventExecutionStatus.WaitingApproval)).ToListAsync())
            {
                execution.Status = AgentEventExecutionStatus.Skipped; execution.LockedAt = null; execution.CompletedAt = now;
                execution.ErrorMessage = "项目恢复不补跑历史事件。"; execution.UpdatedAt = now;
            }
            checkpoint.LastSuccessfulSummaryAt = now;
            checkpoint.UpdatedAt = now;
            checkpoint.Version++;
            foreach (var job in await _context.ScheduledJobs.Where(j => j.ProjectId == projectId).ToListAsync())
            {
                var next = now.Date.Add(job.RunAt);
                job.NextRunAt = next <= now ? next.AddDays(1) : next;
                job.UpdatedAt = now;
            }
            _context.ProjectActivityRecords.Add(new ProjectActivityRecord
            {
                ProjectId = projectId, EntityType = ProjectActivityEntityType.Project,
                EntityId = projectId, ChangeType = ProjectActivityChangeType.Restored,
                FieldName = "AutomationResumeBoundary", EntityName = "项目恢复", OccurredAt = now
            });
        }

        private string GetStatusDisplayName(ProjectStatus status)
        {
            return status switch
            {
                ProjectStatus.Active => "活跃",
                ProjectStatus.Archived => "已归档",
                _ => status.ToString()
            };
        }

        /// <summary>
        /// 获取用户角色的中文显示名称
        /// </summary>
        private string GetRoleDisplayName(int projectRole)
        {
            return projectRole switch
            {
                (int)ProjectRole.Admin => "管理员",
                (int)ProjectRole.Member => "成员",
                _ => "未知角色"
            };
        }

        /// <summary>
        /// 添加项目成员
        /// </summary>
        public async Task<bool> AddProjectMemberAsync(
            int projectId,
            int userId,
            int currentUserId,
            UserRole currentUserRole,
            bool makeAdmin = false)
        {
            using var transaction = await BeginTransactionSafelyAsync();
            try
            {
                // 验证项目是否存在且可用
                var project = await _context.Project
                    .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted && p.Status == ProjectStatus.Active);

                if (project == null)
                {
                    _logger.LogWarning("项目 {ProjectId} 不存在/已删除/已归档", projectId);
                    return false;
                }

                // 修复权限检查：系统管理员或项目管理员都可以添加成员
                bool hasPermission = currentUserRole == UserRole.systemAdmin ||
                                   await IsProjectAdminAsync(projectId, currentUserId);

                if (!hasPermission)
                {
                    _logger.LogWarning("用户 {CurrentUserId} 无权限在项目 {ProjectId} 添加成员", currentUserId, projectId);
                    return false;
                }

                // 检查用户是否存在且活跃
                var user = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active);

                if (user == null)
                {
                    _logger.LogWarning("要添加的用户 {UserId} 不存在或不活跃", userId);
                    return false;
                }

                // 检查是否已是成员
                if (await _context.ProjectUsers
                    .AnyAsync(pu => pu.ProjectId == projectId && pu.UserId == userId))
                {
                    _logger.LogWarning("用户 {UserId} 已是项目 {ProjectId} 成员", userId, projectId);
                    return false;
                }

                // 执行添加
                var projectUser = new ProjectUser
                {
                    ProjectId = projectId,
                    UserId = userId,
                    ProjectRole = makeAdmin ? (int)ProjectRole.Admin : (int)ProjectRole.Member
                };

                _context.ProjectUsers.Add(projectUser);

                // 安全地设置当前用户会话变量
                await SetUserContextAsync(currentUserId);

                await _context.SaveChangesAsync();

                // 记录添加成员日志
                await LogProjectOperationAsync(
                    OperationType.更新,
                    projectId,
                    currentUserId,
                    beforeState: $"项目成员数量: {await _context.ProjectUsers.CountAsync(pu => pu.ProjectId == projectId) - 1}",
                    afterState: $"添加用户: {user.RealName}(ID:{userId}) 为{(makeAdmin ? "管理员" : "成员")}, 当前成员数量: {await _context.ProjectUsers.CountAsync(pu => pu.ProjectId == projectId)}"
                );

                if (!IsInMemoryDatabase())
                {
                    await transaction.CommitAsync();
                }

                _logger.LogInformation("成功添加用户 {UserId} 到项目 {ProjectId}，角色：{Role}",
                    userId, projectId, makeAdmin ? "管理员" : "成员");
                return true;
            }
            catch (DbUpdateException ex)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }

                // 记录添加失败日志
                await LogProjectOperationAsync(
                    OperationType.更新,
                    projectId,
                    currentUserId,
                    beforeState: $"添加成员参数: 用户ID={userId}, 是否管理员={makeAdmin}",
                    afterState: $"添加失败: {ex.Message}",
                    status: OperationStatus.失败
                );

                _logger.LogError(ex, "添加成员到项目 {ProjectId} 失败", projectId);
                return false;
            }
            catch (Exception)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }
                throw;
            }
        }
        // 添加 SetUserContextAsync 方法
        private async Task SetUserContextAsync(int userId)
        {
            if (IsInMemoryDatabase())
            {
                return;
            }

            // 在生产环境中设置用户上下文
            var user = await _context.Users
                .Where(u => u.Id == userId)
                .Select(u => new { u.UserName, u.RealName })
                .FirstOrDefaultAsync();

            if (user != null)
            {
                await SetMySqlSessionVariablesAsync(
                    userId,
                    user.UserName ?? "system",
                    user.RealName ?? "system");
            }
        }


        /// <summary>
        /// 检查是否可以移除成员（用于前端显示）
        /// </summary>
        public async Task<bool> CanRemoveMemberAsync(int projectId, int targetUserId, int currentUserId, UserRole currentUserRole)
        {
            // 获取项目信息
            var project = await _context.Project
                .Include(p => p.ProjectUsers)
                .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

            if (project == null) return false;

            // 获取管理员数量
            var adminCount = await _context.ProjectUsers
                .CountAsync(pu => pu.ProjectId == projectId && pu.ProjectRole == (int)ProjectRole.Admin);

            // 如果管理员数量 <= 1，任何人都不可以移除
            if (adminCount <= 1)
                return false;

            // 系统管理员可以移除任何人
            if (currentUserRole == UserRole.systemAdmin)
                return true;

            // 普通用户不能移除自己
            if (targetUserId == currentUserId)
                return false;

            // 检查目标用户是否是最后一个管理员
            if (await IsLastAdminAsync(projectId, targetUserId))
                return false;

            return await CanManageProjectAsync(projectId, currentUserId, currentUserRole);
        }

        /// <summary>
        /// 移除项目成员（增强权限检查）
        /// </summary>
        public async Task<bool> RemoveProjectMemberAsync(
            int projectId,
            int targetUserId,
            int currentUserId,
            UserRole currentUserRole)
        {
            using var transaction = await BeginTransactionSafelyAsync();
            try
            {
                // 获取项目信息
                var project = await _context.Project
                    .Include(p => p.ProjectUsers)
                    .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

                if (project == null)
                {
                    _logger.LogWarning("项目 {ProjectId} 不存在", projectId);
                    return false;
                }

                // 检查目标用户是否是项目成员
                var projectUser = await _context.ProjectUsers
                    .FirstOrDefaultAsync(pu => pu.ProjectId == projectId && pu.UserId == targetUserId);

                if (projectUser == null)
                {
                    _logger.LogWarning("用户 {TargetUserId} 不是项目 {ProjectId} 的成员", targetUserId, projectId);
                    return false;
                }

                // 获取管理员数量
                var adminCount = await _context.ProjectUsers
                    .CountAsync(pu => pu.ProjectId == projectId && pu.ProjectRole == (int)ProjectRole.Admin);

                // 检查是否是最后一个管理员
                var isTargetAdmin = projectUser.ProjectRole == (int)ProjectRole.Admin;
                if (isTargetAdmin && adminCount <= 1)
                {
                    _logger.LogWarning("不能移除项目 {ProjectId} 的最后一个管理员", projectId);
                    return false;
                }

                // 如果是移除自己，不需要额外权限检查 - 任何人都可以退出自己
                if (targetUserId == currentUserId)
                {
                    // 检查是否可以退出（管理员需要多个管理员才能退出）
                    if (isTargetAdmin && adminCount <= 1)
                    {
                        _logger.LogWarning("用户 {CurrentUserId} 是项目 {ProjectId} 的唯一管理员，不能退出", currentUserId, projectId);
                        return false;
                    }
                    _logger.LogInformation("用户 {CurrentUserId} 正在退出项目 {ProjectId}", currentUserId, projectId);
                }
                else
                {
                    // 如果是移除他人，需要管理权限
                    if (!await CanManageProjectAsync(projectId, currentUserId, currentUserRole))
                    {
                        _logger.LogWarning("用户 {CurrentUserId} 无权限从项目 {ProjectId} 移除成员", currentUserId, projectId);
                        return false;
                    }
                }

                var targetUser = await _context.Users.FindAsync(targetUserId);
                var originalMemberCount = await _context.ProjectUsers.CountAsync(pu => pu.ProjectId == projectId);
                var userRole = GetRoleDisplayName(projectUser.ProjectRole);

                // 执行移除
                _context.ProjectUsers.Remove(projectUser);

                // 安全地设置用户上下文
                await SetUserContextAsync(currentUserId);

                await _context.SaveChangesAsync();

                // 记录移除成员日志
                var operationType = targetUserId == currentUserId ? "退出项目" : "移除成员";
                await LogProjectOperationAsync(
                    OperationType.删除,
                    projectId,
                    currentUserId,
                    beforeState: $"{operationType}前成员数量: {originalMemberCount}, 用户: {targetUser?.RealName}(ID:{targetUserId}) 角色: {userRole}",
                    afterState: $"{operationType}: {targetUser?.RealName}(ID:{targetUserId}), 当前成员数量: {await _context.ProjectUsers.CountAsync(pu => pu.ProjectId == projectId)}"
                );

                if (!IsInMemoryDatabase())
                {
                    await transaction.CommitAsync();
                }

                _logger.LogInformation("成功从项目 {ProjectId} {OperationType} 用户 {TargetUserId}",
                    projectId, targetUserId == currentUserId ? "退出" : "移除", targetUserId);
                return true;
            }
            catch (DbUpdateException ex)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }

                await LogProjectOperationAsync(
                    OperationType.删除,
                    projectId,
                    currentUserId,
                    beforeState: $"移除成员参数: 用户ID={targetUserId}",
                    afterState: $"移除失败: {ex.Message}",
                    status: OperationStatus.失败
                );

                _logger.LogError(ex, "移除成员失败");
                return false;
            }
            catch (Exception)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }
                throw;
            }
        }

        /// <summary>
        /// 切换成员角色（增强权限检查）
        /// </summary>
        public async Task<bool> ToggleAdminStatusAsync(
            int projectId,
            int targetUserId,
            int currentUserId,
            UserRole currentUserRole)
        {
            using var transaction = await BeginTransactionSafelyAsync();
            try
            {
                // 验证权限
                if (!await CanManageProjectAsync(projectId, currentUserId, currentUserRole))
                {
                    _logger.LogWarning("用户 {CurrentUserId} 无权限修改项目 {ProjectId} 的管理员状态", currentUserId, projectId);
                    return false;
                }

                // 检查目标用户是否是项目成员
                var projectUser = await _context.ProjectUsers
                    .FirstOrDefaultAsync(pu => pu.ProjectId == projectId && pu.UserId == targetUserId);

                if (projectUser == null)
                {
                    _logger.LogWarning("用户 {TargetUserId} 不是项目 {ProjectId} 的成员", targetUserId, projectId);
                    return false;
                }

                // 检查是否是最后一个管理员且试图降级
                if (projectUser.ProjectRole == (int)ProjectRole.Admin)
                {
                    var adminCount = await _context.ProjectUsers
                        .CountAsync(pu => pu.ProjectId == projectId && pu.ProjectRole == (int)ProjectRole.Admin);

                    if (adminCount <= 1)
                    {
                        _logger.LogWarning("不能降级项目 {ProjectId} 的最后一个管理员", projectId);
                        return false;
                    }
                }

                var targetUser = await _context.Users.FindAsync(targetUserId);
                var originalRole = GetRoleDisplayName(projectUser.ProjectRole);

                // 切换管理员状态
                projectUser.ProjectRole = projectUser.ProjectRole == (int)ProjectRole.Admin
                    ? (int)ProjectRole.Member
                    : (int)ProjectRole.Admin;

                var newRole = GetRoleDisplayName(projectUser.ProjectRole);

                // 安全地设置用户上下文
                await SetUserContextAsync(currentUserId);

                await _context.SaveChangesAsync();

                // 记录角色变更日志
                await LogProjectOperationAsync(
                    OperationType.更新,
                    projectId,
                    currentUserId,
                    beforeState: $"用户 {targetUser?.RealName}(ID:{targetUserId}) 角色: {originalRole}",
                    afterState: $"用户 {targetUser?.RealName}(ID:{targetUserId}) 角色变更为: {newRole}"
                );

                if (!IsInMemoryDatabase())
                {
                    await transaction.CommitAsync();
                }

                _logger.LogInformation("成功切换用户 {TargetUserId} 在项目 {ProjectId} 的管理员状态: {OriginalRole} → {NewRole}",
                    targetUserId, projectId, originalRole, newRole);
                return true;
            }
            catch (DbUpdateException ex)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }

                await LogProjectOperationAsync(
                    OperationType.更新,
                    projectId,
                    currentUserId,
                    beforeState: $"角色变更参数: 用户ID={targetUserId}",
                    afterState: $"角色变更失败: {ex.Message}",
                    status: OperationStatus.失败
                );

                _logger.LogError(ex, "切换管理员状态失败");
                return false;
            }
            catch (Exception)
            {
                if (!IsInMemoryDatabase())
                {
                    await transaction.RollbackAsync();
                }
                throw;
            }
        }

        /// <summary>
        /// 检查是否是最后一个管理员
        /// </summary>
        public async Task<bool> IsLastAdminAsync(int projectId, int userId)
        {
            // 检查目标用户是否是管理员
            var isAdmin = await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == projectId &&
                               pu.UserId == userId &&
                               pu.ProjectRole == (int)ProjectRole.Admin);

            if (!isAdmin) return false;

            // 检查是否是最后一个管理员
            var adminCount = await _context.ProjectUsers
                .CountAsync(pu => pu.ProjectId == projectId &&
                                 pu.ProjectRole == (int)ProjectRole.Admin);

            return adminCount <= 1;
        }

        /// <summary>
        /// 检查是否可以移除成员（用于前端显示）
        /// </summary>
        public async Task<bool> CanRemovelMemberAsync(int projectId, int targetUserId, int currentUserId, UserRole currentUserRole)
        {
            // 系统管理员可以移除任何人
            if (currentUserRole == UserRole.systemAdmin)
                return true;

            // 普通用户不能移除自己
            if (targetUserId == currentUserId)
                return false;

            // 检查目标用户是否是最后一个管理员
            if (await IsLastAdminAsync(projectId, targetUserId))
                return false;

            return await CanManageProjectAsync(projectId, currentUserId, currentUserRole);
        }

        /// <summary>
        /// 检查用户是否为项目管理员
        /// </summary>
        public async Task<bool> IsProjectAdminAsync(int projectId, int userId)
        {
            return await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == projectId
                             && pu.UserId == userId
                             && pu.ProjectRole == 0);
        }

        public async Task<bool> UpdateProjectLeaderAsync(
            int projectId,
            int newLeaderUserId,
            int currentUserId,
            UserRole currentUserRole)
        {
            var project = await _context.Project
                .Include(p => p.ProjectUsers)
                .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

            if (project == null)
            {
                _logger.LogWarning("用户 {UserId} 更换项目 {ProjectId} 负责人失败：项目不存在", currentUserId, projectId);
                return false;
            }

            if (currentUserRole != UserRole.systemAdmin && project.LeaderUserId != currentUserId)
            {
                _logger.LogWarning("用户 {UserId} 更换项目 {ProjectId} 负责人失败：无权限（需系统管理员或原负责人）",
                    currentUserId, projectId);
                return false;
            }

            var newLeader = await _context.ApplicationUser
                .FirstOrDefaultAsync(u => u.Id == newLeaderUserId && u.Status == UserStatus.Active);
            if (newLeader == null)
            {
                _logger.LogWarning("用户 {UserId} 更换项目 {ProjectId} 负责人失败：新负责人不存在或不活跃",
                    currentUserId, projectId);
                return false;
            }

            if (!project.ProjectUsers.Any(pu => pu.UserId == newLeaderUserId))
            {
                _logger.LogWarning("用户 {UserId} 更换项目 {ProjectId} 负责人失败：新负责人不是项目成员",
                    currentUserId, projectId);
                return false;
            }

            if (newLeaderUserId == project.LeaderUserId)
            {
                _logger.LogWarning("用户 {UserId} 更换项目 {ProjectId} 负责人失败：新负责人与原负责人相同",
                    currentUserId, projectId);
                return false;
            }

            try
            {
                var oldLeaderId = project.LeaderUserId;
                project.LeaderUserId = newLeaderUserId;
                await _context.SaveChangesAsync();

                var newLeaderProjectUser = project.ProjectUsers.First(pu => pu.UserId == newLeaderUserId);
                newLeaderProjectUser.ProjectRole = (int)ProjectRole.Admin;
                await _context.SaveChangesAsync();

                _logger.LogInformation("用户 {UserId} 成功将项目 {ProjectId} 负责人从 {OldLeaderId} 更换为 {NewLeaderId}",
                    currentUserId, projectId, oldLeaderId, newLeaderUserId);
                return true;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "用户 {UserId} 更换项目 {ProjectId} 负责人失败：数据库异常", currentUserId, projectId);
                return false;
            }
        }

        public async Task<bool> IsProjectModifiableAsync(int projectId)
        {
            return await _context.Project
                .AnyAsync(p => p.Id == projectId &&
                              !p.IsDeleted &&
                              p.Status == ProjectStatus.Active);
        }

        public async Task<(List<ProjectListDto> Projects, int TotalCount)> GetPublicProjectsAsync(
            int pageIndex,
            int pageSize,
            string? keyword = null,
            bool? isArchivedFilter = null)
        {
            pageIndex = Math.Max(1, pageIndex);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = _context.Project
                .Where(p => !p.IsDeleted && p.IsEncrypted == '0')
                .Select(p => new ProjectListDto
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = p.Description ?? string.Empty,
                    CreatedAt = p.CreatedAt,
                    CreatedByUserName = p.CreatedByUser.UserName ?? string.Empty,
                    Status = p.Status,
                    IsEncrypted = p.IsEncrypted,
                    CanModify = p.Status == ProjectStatus.Active
                });

            if (!string.IsNullOrEmpty(keyword))
            {
                query = query.Where(p =>
                    EF.Functions.Like(p.Name, $"%{keyword}%") ||
                    EF.Functions.Like(p.Description ?? string.Empty, $"%{keyword}%"));
            }

            if (isArchivedFilter.HasValue)
            {
                query = query.Where(p => p.Status ==
                    (isArchivedFilter.Value ? ProjectStatus.Archived : ProjectStatus.Active));
            }

            var totalCount = await query.CountAsync();
            var projects = await query
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Skip((int)Math.Min((long)(pageIndex - 1) * pageSize, int.MaxValue))
                .Take(pageSize)
                .ToListAsync();

            return (projects, totalCount);
        }

        /// <summary>
        /// 校验用户是否有项目管理权限（仅基于角色，不依赖创建者身份）
        /// </summary>
        public async Task<bool> CanManageProjectAsync(
            int projectId,
            int userId,
            UserRole userRole)
        {
            var project = await _context.Project
               .Include(p => p.ProjectUsers)
               .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);

            if (project == null) return false;

            // 系统管理员直接通过
            if (userRole == UserRole.systemAdmin) return true;

            // 项目管理员检查：仅基于 ProjectRole.Admin，不检查创建者身份
            return await IsProjectAdminAsync(projectId, userId);
        }

        /// <summary>
        /// 检查用户是否可以退出项目
        /// </summary>
        public async Task<bool> CanExitProjectAsync(int projectId, int userId)
        {
            // 检查用户是否是项目成员
            var isMember = await _context.ProjectUsers
                .AnyAsync(pu => pu.ProjectId == projectId && pu.UserId == userId);

            if (!isMember) return false;

            // 如果是普通成员，可以随时退出
            var isAdmin = await IsProjectAdminAsync(projectId, userId);
            if (!isAdmin) return true;

            // 如果是管理员，检查管理员数量
            var adminCount = await _context.ProjectUsers
                .CountAsync(pu => pu.ProjectId == projectId && pu.ProjectRole == (int)ProjectRole.Admin);

            return adminCount > 1; // 只有多个管理员时才能退出
        }

        /// <summary>
        /// 查询项目（用于前端校验项目是否存在）
        /// </summary>
        public async Task<Project?> GetProjectByIdAsync(int projectId)
        {
            return await _context.Project
               .FirstOrDefaultAsync(p => p.Id == projectId && !p.IsDeleted);
        }

        /// <summary>
        /// 增强的日志记录方法：确保操作日志完整性和一致性
        /// </summary>
        private async Task LogProjectOperationAsync(
            OperationType operationType,
            int projectId,
            int operatorUserId,
            string beforeState = "",
            string afterState = "",
            OperationStatus status = OperationStatus.成功)
        {
            try
            {
                // 获取操作人信息
                var operatorInfo = await _context.Users
                    .Where(u => u.Id == operatorUserId)
                    .Select(u => new
                    {
                        UserName = u.UserName ?? "unknown",
                        RealName = u.RealName ?? u.UserName ?? "未知用户"
                    })
                    .FirstOrDefaultAsync();

                if (operatorInfo == null)
                {
                    _logger.LogWarning("操作人ID {UserId} 不存在，使用默认信息记录日志", operatorUserId);
                    operatorInfo = new { UserName = "unknown", RealName = "未知用户" };
                }

                // 获取项目信息
                string? projectName = null;
                if (projectId > 0)
                {
                    projectName = await _context.Project
                        .Where(p => p.Id == projectId)
                        .Select(p => p.Name)
                        .FirstOrDefaultAsync() ?? "未知项目";
                }

                // 构造完整的日志记录
                var logEntry = new ChangeLog
                {
                    OperationType = operationType,
                    OperationStatus = status,
                    OperatedAt = DateTime.UtcNow,
                    OperationTarget = OperationTarget.项目,
                    OperatedByUserId = operatorUserId,
                    OperatedByUserName = operatorInfo.UserName,
                    OperatedByRealName = operatorInfo.RealName,
                    ProjectId = projectId > 0 ? projectId : null,
                    ProjectName = projectName,
                    BeforeContent = beforeState,
                    AfterContent = afterState
                };

                _context.ChangeLogs.Add(logEntry);
                await _context.SaveChangesAsync();

                _logger.LogDebug("操作日志记录成功：{OperationType} 项目 {ProjectId}，操作人 {UserId}",
                    operationType, projectId, operatorUserId);
            }
            catch (Exception ex)
            {
                // 日志记录失败不应影响主业务，但需要记录错误
                _logger.LogError(ex, "操作日志记录失败：项目ID {ProjectId}，操作类型 {OperationType}",
                    projectId, operationType);
            }
        }
    }
}
