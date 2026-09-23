using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AgentAutomationPhaseOne : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AgentAssignmentVersion",
                table: "tasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AgentDefinitionId",
                table: "tasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AgentExecutionStatus",
                table: "tasks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AgentLastError",
                table: "tasks",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "AgentLastRunAt",
                table: "tasks",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConcurrencyVersion",
                table: "tasks",
                type: "int",
                nullable: false,
                defaultValue: 1);

            // 兼容历史上仅保存 AgentName 文本的数字员工任务：优先按 AgentKey 或名称绑定真实定义。
            migrationBuilder.Sql("""
                UPDATE `tasks` AS `t`
                SET `AgentDefinitionId` = (
                    SELECT MIN(`a`.`Id`)
                    FROM `agent_definitions` AS `a`
                    WHERE `a`.`AgentKey` = `t`.`AgentName` OR `a`.`Name` = `t`.`AgentName`
                )
                WHERE `t`.`AssigneeType` = 1 AND `t`.`AgentName` IS NOT NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE `tasks`
                SET `AgentAssignmentVersion` = 1, `AgentExecutionStatus` = 1
                WHERE `AssigneeType` = 1
                  AND `AgentDefinitionId` IS NOT NULL
                  AND `IsDeleted` = 0
                  AND `Status` NOT IN (2, 3);
                """);

            migrationBuilder.CreateTable(
                name: "agent_work_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    AiSessionId = table.Column<int>(type: "int", nullable: true),
                    WaitingApprovalRequestId = table.Column<int>(type: "int", nullable: true),
                    TriggerType = table.Column<int>(type: "int", nullable: false),
                    TriggerEntityId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CauseChainId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
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
                    table.PrimaryKey("PK_agent_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_work_items_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_work_items_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_agent_work_items_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_work_items_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_work_items_approval_requests_WaitingApprovalRequestId",
                        column: x => x.WaitingApprovalRequestId,
                        principalTable: "approval_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_work_items_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_AgentDefinitionId",
                table: "tasks",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_AgentDefinitionId",
                table: "agent_work_items",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_AiSessionId",
                table: "agent_work_items",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_IdempotencyKey",
                table: "agent_work_items",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_ProjectId",
                table: "agent_work_items",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_RequestedByUserId",
                table: "agent_work_items",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_Status_NextRunAt",
                table: "agent_work_items",
                columns: new[] { "Status", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_TaskId_CreatedAt",
                table: "agent_work_items",
                columns: new[] { "TaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_work_items_WaitingApprovalRequestId",
                table: "agent_work_items",
                column: "WaitingApprovalRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_tasks_agent_definitions_AgentDefinitionId",
                table: "tasks",
                column: "AgentDefinitionId",
                principalTable: "agent_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tasks_agent_definitions_AgentDefinitionId",
                table: "tasks");

            migrationBuilder.DropTable(
                name: "agent_work_items");

            migrationBuilder.DropIndex(
                name: "IX_tasks_AgentDefinitionId",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AgentAssignmentVersion",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AgentDefinitionId",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AgentExecutionStatus",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AgentLastError",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AgentLastRunAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "ConcurrencyVersion",
                table: "tasks");
        }
    }
}
