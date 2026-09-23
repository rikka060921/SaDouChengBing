using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentExecutionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionPlan",
                table: "agent_work_items",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // 先填充已有行，再收紧非空约束；兼容有历史队列数据的数据库。
            migrationBuilder.Sql("UPDATE agent_work_items SET ExecutionPlan = '' WHERE ExecutionPlan IS NULL;");
            migrationBuilder.AlterColumn<string>(
                name: "ExecutionPlan",
                table: "agent_work_items",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PlanAgentVersion",
                table: "agent_work_items",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PlanApprovedAt",
                table: "agent_work_items",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlanApprovedByUserId",
                table: "agent_work_items",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanContextHash",
                table: "agent_work_items",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PlanFeedback",
                table: "agent_work_items",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "PlanRevision",
                table: "agent_work_items",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PlanningSessionId",
                table: "agent_work_items",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresPlan",
                table: "agent_work_items",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionPlan",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanAgentVersion",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanApprovedAt",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanApprovedByUserId",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanContextHash",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanFeedback",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanRevision",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "PlanningSessionId",
                table: "agent_work_items");

            migrationBuilder.DropColumn(
                name: "RequiresPlan",
                table: "agent_work_items");
        }
    }
}
