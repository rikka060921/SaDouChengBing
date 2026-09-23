using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentEventAutomationSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_event_subscriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EventType = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    PromptTemplate = table.Column<string>(type: "text", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CooldownSeconds = table.Column<int>(type: "int", nullable: false),
                    MaxAttempts = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_event_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_event_subscriptions_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_event_subscriptions_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_event_subscriptions_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "agent_event_executions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    SubscriptionId = table.Column<int>(type: "int", nullable: false),
                    EventBusMessageId = table.Column<long>(type: "bigint", nullable: false),
                    AgentDefinitionId = table.Column<int>(type: "int", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: true),
                    TaskId = table.Column<int>(type: "int", nullable: true),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    AiSessionId = table.Column<int>(type: "int", nullable: true),
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
                    table.PrimaryKey("PK_agent_event_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_AspNetUsers_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_agent_definitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "agent_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_agent_event_subscriptions_Subscriptio~",
                        column: x => x.SubscriptionId,
                        principalTable: "agent_event_subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_ai_sessions_AiSessionId",
                        column: x => x.AiSessionId,
                        principalTable: "ai_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_approval_requests_WaitingApprovalRequ~",
                        column: x => x.WaitingApprovalRequestId,
                        principalTable: "approval_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_event_bus_messages_EventBusMessageId",
                        column: x => x.EventBusMessageId,
                        principalTable: "event_bus_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_agent_event_executions_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_AgentDefinitionId",
                table: "agent_event_executions",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_AiSessionId",
                table: "agent_event_executions",
                column: "AiSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_EventBusMessageId",
                table: "agent_event_executions",
                column: "EventBusMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_ProjectId",
                table: "agent_event_executions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_RequestedByUserId",
                table: "agent_event_executions",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_Status_NextRunAt",
                table: "agent_event_executions",
                columns: new[] { "Status", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_SubscriptionId_EventBusMessageId",
                table: "agent_event_executions",
                columns: new[] { "SubscriptionId", "EventBusMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_TaskId",
                table: "agent_event_executions",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_executions_WaitingApprovalRequestId",
                table: "agent_event_executions",
                column: "WaitingApprovalRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_subscriptions_AgentDefinitionId",
                table: "agent_event_subscriptions",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_subscriptions_CreatedByUserId",
                table: "agent_event_subscriptions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_subscriptions_EventType_IsEnabled",
                table: "agent_event_subscriptions",
                columns: new[] { "EventType", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_agent_event_subscriptions_ProjectId",
                table: "agent_event_subscriptions",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_event_executions");

            migrationBuilder.DropTable(
                name: "agent_event_subscriptions");
        }
    }
}
