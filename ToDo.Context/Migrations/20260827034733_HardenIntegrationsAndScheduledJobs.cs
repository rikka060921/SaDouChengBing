using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToDo.Context.Migrations
{
    /// <inheritdoc />
    public partial class HardenIntegrationsAndScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                table: "scheduled_jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsRunning",
                table: "scheduled_jobs",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "scheduled_jobs",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LockToken",
                table: "scheduled_jobs",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "scheduled_jobs",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "integration_call_records",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "integration_call_records",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "DurationMs",
                table: "integration_call_records",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCategory",
                table: "integration_call_records",
                type: "varchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "HttpStatusCode",
                table: "integration_call_records",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "integration_call_records",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsSuccess",
                table: "integration_call_records",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedAt",
                table: "event_bus_messages",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_jobs_IsEnabled_IsRunning_NextRunAt",
                table: "scheduled_jobs",
                columns: new[] { "IsEnabled", "IsRunning", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_integration_call_records_CreatedAt",
                table: "integration_call_records",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_integration_call_records_Provider_IdempotencyKey",
                table: "integration_call_records",
                columns: new[] { "Provider", "IdempotencyKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_scheduled_jobs_IsEnabled_IsRunning_NextRunAt",
                table: "scheduled_jobs");

            migrationBuilder.DropIndex(
                name: "IX_integration_call_records_CreatedAt",
                table: "integration_call_records");

            migrationBuilder.DropIndex(
                name: "IX_integration_call_records_Provider_IdempotencyKey",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "IsRunning",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "LockToken",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "scheduled_jobs");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "ErrorCategory",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "HttpStatusCode",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "IsSuccess",
                table: "integration_call_records");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "event_bus_messages");
        }
    }
}
