using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ToDo.Context;
using ToDo.Entities;

namespace ToDo.Domain
{
    public class TestTaskGroupDomainService : TaskGroupDomainService
    {
        public TestTaskGroupDomainService(ApplicationDbContext context, ILogger<ProjectDomain> logger)
            : base(context, logger) { }
        public override Task LogTaskGroupOperationAsync(
            OperationType operationType,
            OperationTarget target,
            int operatorUserId,
            string? beforeState = null,
            string? afterState = null,
            OperationStatus status = OperationStatus.成功,
            int? projectId = null,
            string? projectName = null,
            int? targetId = null,
            string? targetName = null)
        {
            return Task.CompletedTask; // 测试中直接忽略日志操作
        }
    }
}
