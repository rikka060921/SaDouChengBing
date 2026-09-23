using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using ToDo.Context;

namespace ToDo.Razor.Filters;

/// <summary>Show a business rejection instead of a 500 when a stale form writes to an archived project.</summary>
public sealed class ProjectReadOnlyExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not (InvalidOperationException or UnauthorizedAccessException)
            || context.Exception.Message != ProjectLifecycleRules.ReadOnlyMessage) return;
        context.Result = new BadRequestObjectResult(new { success = false, message = ProjectLifecycleRules.ReadOnlyMessage });
        context.ExceptionHandled = true;
    }
}
