using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAgentAutomationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutomatedReviewJson",
                table: "approval_requests",
                type: "text",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PolicyName",
                table: "approval_requests",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ReviewMode",
                table: "approval_requests",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "RiskLevel",
                table: "approval_requests",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "AgentVersion",
                table: "ai_sessions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<long>(
                name: "DurationMilliseconds",
                table: "ai_sessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCost",
                table: "ai_sessions",
                type: "decimal(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewMode",
                table: "agent_tool_permissions",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "agent_tool_calls",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ReviewMode",
                table: "agent_tool_calls",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReason",
                table: "agent_tool_calls",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "RiskLevel",
                table: "agent_tool_calls",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<string>(
                name: "ConditionField",
                table: "agent_event_subscriptions",
                type: "varchar(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ConditionOperator",
                table: "agent_event_subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ConditionValue",
                table: "agent_event_subscriptions",
                type: "varchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "DailyExecutionLimit",
                table: "agent_event_subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "MaxSteps",
                table: "agent_event_subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "MaxTokenBudget",
                table: "agent_event_subscriptions",
                type: "int",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "ConsumedTokens",
                table: "agent_event_executions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxTokenBudget",
                table: "agent_event_executions",
                type: "int",
                nullable: false,
                defaultValue: 20000);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "agent_definitions",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // 历史记录必须先回填，再创建唯一索引；默认空字符串会让第二条旧记录迁移失败。
            migrationBuilder.Sql("UPDATE agent_tool_calls SET IdempotencyKey = LOWER(SHA2(CONCAT('legacy:', Id), 256)) WHERE IdempotencyKey = '';");

            // 把旧版 RequiresApproval 映射到新三层审核策略。中风险新增操作默认 AI 审核，高风险始终人工。
            migrationBuilder.Sql("UPDATE agent_tool_permissions SET ReviewMode = CASE WHEN ToolName IN ('task.create','meeting.action.create','report.create') THEN 1 WHEN ToolName IN ('task.update','project.document.write','project.update') THEN 2 WHEN RequiresApproval = 1 THEN 2 ELSE 0 END;");
            migrationBuilder.Sql("UPDATE agent_tool_calls SET RiskLevel = CASE WHEN ToolName IN ('task.add_comment','context.read') THEN 0 WHEN ToolName IN ('task.create','meeting.action.create','report.create') THEN 1 ELSE 2 END, ReviewMode = CASE WHEN ToolName IN ('task.create','meeting.action.create','report.create') THEN 1 WHEN ToolName IN ('task.add_comment','context.read') AND RequiresApproval = 0 THEN 0 ELSE 2 END;");
            migrationBuilder.Sql("UPDATE approval_requests SET RiskLevel = CASE WHEN ActionType IN ('AgentTaskCreate','AgentMeetingActionCreate','AgentDailyReportCreate') THEN 1 ELSE 2 END, ReviewMode = 2 WHERE SourceType = 'AgentToolCall';");

            migrationBuilder.CreateTable(
                name: "agent_definition_versions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ChangedByUserId = table.Column<int>(type: "int", nullable: true),
                    SnapshotJson = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_definition_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_definition_versions_AspNetUsers_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_definition_versions_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_run_jobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AiSessionId = table.Column<int>(type: "int", nullable: false),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    WaitingApprovalRequestId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ResultSummary = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorMessage = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    StepCount = table.Column<int>(type: "int", nullable: false),
                    MaxSteps = table.Column<int>(type: "int", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LockedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_run_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_approval_requests_WaitingApprovalRequestId",
                        column: x => x.WaitingApprovalRequestId,
                        principalTable: "approval_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_run_jobs_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_tool_calls_IdempotencyKey",
                table: "agent_tool_calls",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_definition_versions_AgentDefinitionId_Version",
                table: "agent_definition_versions",
                columns: new[] { "AgentDefinitionId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_definition_versions_ChangedByUserId",
                table: "agent_definition_versions",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_AgentDefinitionId",
                table: "agent_run_jobs",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_AiSessionId_CreatedAt",
                table: "agent_run_jobs",
                columns: new[] { "AiSessionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_ProjectId",
                table: "agent_run_jobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_RequestedByUserId",
                table: "agent_run_jobs",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_Status_NextRunAt",
                table: "agent_run_jobs",
                columns: new[] { "Status", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_TaskId",
                table: "agent_run_jobs",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_jobs_WaitingApprovalRequestId",
                table: "agent_run_jobs",
                column: "WaitingApprovalRequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_definition_versions");

            migrationBuilder.DropTable(
                name: "agent_run_jobs");

            migrationBuilder.DropIndex(
                name: "IX_agent_tool_calls_IdempotencyKey",
                table: "agent_tool_calls");

            migrationBuilder.DropColumn(
                name: "AutomatedReviewJson",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "PolicyName",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "ReviewMode",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "approval_requests");

            migrationBuilder.DropColumn(
                name: "AgentVersion",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "DurationMilliseconds",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "EstimatedCost",
                table: "ai_sessions");

            migrationBuilder.DropColumn(
                name: "ReviewMode",
                table: "agent_tool_permissions");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "agent_tool_calls");

            migrationBuilder.DropColumn(
                name: "ReviewMode",
                table: "agent_tool_calls");

            migrationBuilder.DropColumn(
                name: "ReviewReason",
                table: "agent_tool_calls");

            migrationBuilder.DropColumn(
                name: "RiskLevel",
                table: "agent_tool_calls");

            migrationBuilder.DropColumn(
                name: "ConditionField",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "ConditionOperator",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "ConditionValue",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "DailyExecutionLimit",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "MaxSteps",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "MaxTokenBudget",
                table: "agent_event_subscriptions");

            migrationBuilder.DropColumn(
                name: "ConsumedTokens",
                table: "agent_event_executions");

            migrationBuilder.DropColumn(
                name: "MaxTokenBudget",
                table: "agent_event_executions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "agent_definitions");
        }
    }
}
