using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Data;
using System.Data.Common;
using System;
using System.Linq;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Razor.Data
{
    public static class SeedData
    {
        public static async Task Initialize(IServiceProvider serviceProvider)
        {
            var context = serviceProvider.GetRequiredService<ApplicationDbContext>();
            var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole<int>>>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();

            // Visual Studio 直接运行时自动应用已提交的迁移。
            // 旧版本曾使用 EnsureCreated，数据库可能没有迁移历史；这种情况只补第二阶段迁移。
            try
            {
                if (await IsDatabaseEmptyAsync(context))
                {
                    await BootstrapFreshDatabaseAsync(context);
                }
                else
                {
                    await RepairInterruptedDocumentCategoryMigrationAsync(context);
                    await context.Database.MigrateAsync();
                }
            }
            catch (Exception migrationException)
            {
                try
                {
                    await EnsureMigrationHistoryAsync(context);
                    await context.Database.ExecuteSqlRawAsync(
                        "INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ('20260719113651_FirstPhaseWorkflow', '8.0.17')");
                    await RepairInterruptedDocumentCategoryMigrationAsync(context);
                    await context.Database.MigrateAsync();
                }
                catch (Exception fallbackException)
                {
                    Console.Error.WriteLine($"数据库迁移失败：{migrationException.Message}; 兼容迁移失败：{fallbackException.Message}");
                    throw new InvalidOperationException("数据库迁移失败，应用已停止启动以避免使用不完整的数据结构。", fallbackException);
                }
            }

            // 创建角色
            string[] roleNames = { "systemAdmin", "teamMember" };
            foreach (var roleName in roleNames)
            {
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    await roleManager.CreateAsync(new IdentityRole<int>(roleName));
                }
            }

            // 历史账号创建于启用登录锁定策略之前，需要一次性补齐该标记。
            var usersWithoutLockout = await userManager.Users
                .Where(user => !user.LockoutEnabled)
                .ToListAsync();
            foreach (var user in usersWithoutLockout)
            {
                var result = await userManager.SetLockoutEnabledAsync(user, true);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"启用账号 {user.UserName} 的登录锁定失败：{string.Join(", ", result.Errors.Select(error => error.Description))}");
                }
            }



            // 演示账号只允许在开发环境显式开启，密码必须来自 User Secrets 或环境变量。
            // 生产环境中的真实账号统一从用户管理页面维护。
            if (environment.IsDevelopment()
                && bool.TryParse(configuration["SeedData:CreateDemoUsers"], out var createDemoUsers)
                && createDemoUsers)
            {
                var demoPassword = configuration["SeedData:DemoPassword"];
                if (string.IsNullOrWhiteSpace(demoPassword))
                    throw new InvalidOperationException("已开启演示账号，但未通过 User Secrets 或环境变量配置 SeedData:DemoPassword");

                // 新增三个系统管理员
                var zhaodongUser = new ApplicationUser
                {
                    UserName = "zhaodong",
                    Email = "zhaodong@example.com",
                    RealName = "赵冬",
                    Gender = Gender.Male,
                    Role = UserRole.systemAdmin,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000002"
                };

                var qiaokuanUser = new ApplicationUser
                {
                    UserName = "qiaokuan",
                    Email = "qiaokuan@example.com",
                    RealName = "乔宽",
                    Gender = Gender.Male,
                    Role = UserRole.systemAdmin,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000003"
                };

                var maizhiyuUser = new ApplicationUser
                {
                    UserName = "maizhiyu",
                    Email = "maizhiyu@example.com",
                    RealName = "买志玉",
                    Gender = Gender.Male,
                    Role = UserRole.systemAdmin,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000004"
                };

                // 创建五个普通用户
                var geziyeUser = new ApplicationUser
                {
                    UserName = "geziye",
                    Email = "geziye@example.com",
                    RealName = "葛紫烨",
                    Gender = Gender.Male,
                    Role = UserRole.teamMember,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "18337098371"
                };

                var wangqiUser = new ApplicationUser
                {
                    UserName = "wangqi",
                    Email = "wangqi@example.com",
                    RealName = "王琦",
                    Gender = Gender.Male,
                    Role = UserRole.teamMember,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000006"
                };

                var zhaokeweiUser = new ApplicationUser
                {
                    UserName = "zhaokewei",
                    Email = "zhaokewei@example.com",
                    RealName = "赵珂薇",
                    Gender = Gender.Male,
                    Role = UserRole.teamMember,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000007"
                };

                var zhangweizheUser = new ApplicationUser
                {
                    UserName = "zhangweizhe",
                    Email = "zhangweizhe@example.com",
                    RealName = "张文辄",
                    Gender = Gender.Male,
                    Role = UserRole.teamMember,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000008"
                };

                var zhuyaozongUser = new ApplicationUser
                {
                    UserName = "zhuyaozong",
                    Email = "zhuyaozong@example.com",
                    RealName = "朱耀宗",
                    Gender = Gender.Male,
                    Role = UserRole.teamMember,
                    Status = UserStatus.Active,
                    EmailConfirmed = true,
                    CreatedAt = AppTime.Now,
                    PhoneNumber = "13800000009"
                };

                // 创建三个系统管理员用户
                await CreateUserAsync(userManager, zhaodongUser, demoPassword, "systemAdmin");
                await CreateUserAsync(userManager, qiaokuanUser, demoPassword, "systemAdmin");
                await CreateUserAsync(userManager, maizhiyuUser, demoPassword, "systemAdmin");

                // 创建五个普通用户
                await CreateUserAsync(userManager, geziyeUser, demoPassword, "teamMember");
                await CreateUserAsync(userManager, wangqiUser, demoPassword, "teamMember");
                await CreateUserAsync(userManager, zhaokeweiUser, demoPassword, "teamMember");
                await CreateUserAsync(userManager, zhangweizheUser, demoPassword, "teamMember");
                await CreateUserAsync(userManager, zhuyaozongUser, demoPassword, "teamMember");
            }

            // 自动修复旧草稿的AgentDisplayName（将英文Key更新为中文名）
            await FixAgentDisplayNamesAsync(context);
        }

        private static async Task FixAgentDisplayNamesAsync(ApplicationDbContext context)
        {
            var agentNameMap = new Dictionary<string, string>
            {
                ["project-document-summary"] = "项目资料摘要 Agent",
                ["project-workbench"] = "项目工作台 Agent",
                ["meeting-minutes"] = "会议纪要助手",
                ["risk-review"] = "任务风险审查 Agent",
                ["daily-report"] = "日报助手",
                ["red-team"] = "红方策略助手",
                ["blue-team"] = "蓝方防守助手",
                ["moderator"] = "红蓝主持人",
                ["adversarial-judge"] = "对抗裁判"
            };

            var drafts = await context.AiDocumentDrafts.ToListAsync();
            var updated = false;

            foreach (var draft in drafts)
            {
                if (agentNameMap.ContainsKey(draft.AgentKey) &&
                    draft.AgentDisplayName != agentNameMap[draft.AgentKey])
                {
                    draft.AgentDisplayName = agentNameMap[draft.AgentKey];
                    updated = true;
                }
            }

            if (updated)
            {
                await context.SaveChangesAsync();
            }
        }

        /// <summary>
        /// Repairs the exact state left by the original AddDocumentCategory migration:
        /// MySQL committed its earlier DDL, then rejected dropping an index required by
        /// the ProjectId foreign key. The migration history row was therefore never added.
        /// </summary>
        private static async Task RepairInterruptedDocumentCategoryMigrationAsync(ApplicationDbContext context)
        {
            const string previousMigration = "20260727174451_ConfigurableAgentFramework";
            const string targetMigration = "20260731130336_AddDocumentCategory";

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync();

            try
            {
                if (!await TableExistsAsync(connection, "__EFMigrationsHistory")
                    || !await MigrationAppliedAsync(connection, previousMigration)
                    || await MigrationAppliedAsync(connection, targetMigration)
                    || !await TableExistsAsync(connection, "document_categories"))
                {
                    return;
                }

                await EnsureColumnAsync(connection, "project_documents", "CategoryId",
                    "ALTER TABLE `project_documents` ADD COLUMN `CategoryId` int NULL;");
                await EnsureColumnAsync(connection, "project_documents", "CategoryName",
                    "ALTER TABLE `project_documents` ADD COLUMN `CategoryName` varchar(50) NULL;");
                await EnsureColumnAsync(connection, "agent_document_permissions", "CategoryId",
                    "ALTER TABLE `agent_document_permissions` ADD COLUMN `CategoryId` int NULL;");
                await EnsureColumnAsync(connection, "agent_document_access_logs", "CategoryId",
                    "ALTER TABLE `agent_document_access_logs` ADD COLUMN `CategoryId` int NULL;");

                // The original migration stored the enum annotation as a string, so
                // an interrupted database can be left without AUTO_INCREMENT.
                await ExecuteSchemaCommandAsync(connection,
                    "ALTER TABLE `document_categories` MODIFY COLUMN `Id` int NOT NULL AUTO_INCREMENT;");

                await EnsureIndexAsync(connection, "agent_document_permissions",
                    "IX_agent_document_permissions_CategoryId",
                    "CREATE INDEX `IX_agent_document_permissions_CategoryId` ON `agent_document_permissions` (`CategoryId`);");
                await EnsureIndexAsync(connection, "agent_document_permissions",
                    "IX_agent_doc_perm_proj_agent_cat_catid",
                    "CREATE UNIQUE INDEX `IX_agent_doc_perm_proj_agent_cat_catid` ON `agent_document_permissions` (`ProjectId`, `AgentKey`, `Category`, `CategoryId`);");
                await EnsureForeignKeyAsync(connection, "agent_document_permissions",
                    "FK_agent_document_permissions_document_categories_CategoryId",
                    "ALTER TABLE `agent_document_permissions` ADD CONSTRAINT `FK_agent_document_permissions_document_categories_CategoryId` FOREIGN KEY (`CategoryId`) REFERENCES `document_categories` (`Id`) ON DELETE RESTRICT;");

                if (await IndexExistsAsync(connection, "agent_document_permissions",
                    "IX_agent_document_permissions_ProjectId_AgentKey_Category"))
                {
                    await ExecuteSchemaCommandAsync(connection,
                        "DROP INDEX `IX_agent_document_permissions_ProjectId_AgentKey_Category` ON `agent_document_permissions`;");
                }

                await ExecuteSchemaCommandAsync(connection,
                    $"INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`) VALUES ('{targetMigration}', '8.0.17');");
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync();
            }
        }

        private static async Task EnsureColumnAsync(DbConnection connection, string tableName, string columnName, string sql)
        {
            if (!await ColumnExistsAsync(connection, tableName, columnName))
                await ExecuteSchemaCommandAsync(connection, sql);
        }

        private static async Task EnsureIndexAsync(DbConnection connection, string tableName, string indexName, string sql)
        {
            if (!await IndexExistsAsync(connection, tableName, indexName))
                await ExecuteSchemaCommandAsync(connection, sql);
        }

        private static async Task EnsureForeignKeyAsync(DbConnection connection, string tableName, string constraintName, string sql)
        {
            if (!await SchemaObjectExistsAsync(connection,
                "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND CONSTRAINT_NAME = @objectName AND CONSTRAINT_TYPE = 'FOREIGN KEY';",
                tableName, constraintName))
            {
                await ExecuteSchemaCommandAsync(connection, sql);
            }
        }

        private static Task<bool> TableExistsAsync(DbConnection connection, string tableName) =>
            SchemaObjectExistsAsync(connection,
                "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName;",
                tableName, string.Empty);

        private static Task<bool> ColumnExistsAsync(DbConnection connection, string tableName, string columnName) =>
            SchemaObjectExistsAsync(connection,
                "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = @objectName;",
                tableName, columnName);

        private static Task<bool> IndexExistsAsync(DbConnection connection, string tableName, string indexName) =>
            SchemaObjectExistsAsync(connection,
                "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND INDEX_NAME = @objectName;",
                tableName, indexName);

        private static async Task<bool> MigrationAppliedAsync(DbConnection connection, string migrationId)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE `MigrationId` = @migrationId;";
            AddParameter(command, "@migrationId", migrationId);
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }

        private static async Task<bool> SchemaObjectExistsAsync(
            DbConnection connection,
            string sql,
            string tableName,
            string objectName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@tableName", tableName);
            if (sql.Contains("@objectName", StringComparison.Ordinal))
                AddParameter(command, "@objectName", objectName);
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }

        private static async Task ExecuteSchemaCommandAsync(DbConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static async Task EnsureMigrationHistoryAsync(ApplicationDbContext context)
        {
            await context.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
                    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
                    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
                    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
                ) CHARACTER SET=utf8mb4;");
        }

        /// <summary>
        /// The historical migration chain starts from a legacy schema and cannot build
        /// a brand-new database by itself. For a truly empty schema, create the current
        /// model once and record every committed migration as applied. Existing schemas
        /// always continue through normal incremental migrations.
        /// </summary>
        private static async Task BootstrapFreshDatabaseAsync(ApplicationDbContext context)
        {
            await context.Database.EnsureCreatedAsync();
            await EnsureMigrationHistoryAsync(context);

            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync();

            try
            {
                foreach (var migrationId in context.Database.GetMigrations())
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = @"
                        INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
                        VALUES (@migrationId, @productVersion);";
                    AddParameter(command, "@migrationId", migrationId);
                    AddParameter(command, "@productVersion", "8.0.17");
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync();
            }
        }

        private static async Task<bool> IsDatabaseEmptyAsync(ApplicationDbContext context)
        {
            var connection = context.Database.GetDbConnection();
            var shouldClose = connection.State != ConnectionState.Open;
            if (shouldClose) await connection.OpenAsync();

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT COUNT(*)
                    FROM information_schema.TABLES
                    WHERE TABLE_SCHEMA = DATABASE();";
                return Convert.ToInt64(await command.ExecuteScalarAsync()) == 0;
            }
            finally
            {
                if (shouldClose) await connection.CloseAsync();
            }
        }

        private static async Task CreateUserAsync(UserManager<ApplicationUser> userManager,
            ApplicationUser user, string password, string role)
        {
            var existing = await userManager.FindByNameAsync(user.UserName ?? string.Empty)
                ?? (string.IsNullOrWhiteSpace(user.Email) ? null : await userManager.FindByEmailAsync(user.Email));
            if (existing == null)
            {
                var result = await userManager.CreateAsync(user, password);
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(user, role);
                }
                else
                {
                    throw new Exception($"创建用户{user.UserName}失败: {string.Join(", ", result.Errors.Select(e => e.Description))}");
                }
            }
            else if (!await userManager.IsInRoleAsync(existing, role))
            {
                await userManager.AddToRoleAsync(existing, role);
            }
        }
    }
}
