using Microsoft.AspNetCore.Authorization;
using ToDo.Context;

namespace ToDo.Razor.Pages.DailySummary;

[Authorize]
public sealed class DetailsModel(ApplicationDbContext context, ILogger<DetailsModel> logger) : DailySummaryPageModel(context, logger)
{
    public Task OnGetAsync() => LoadAsync(includeHistory: false);
}
