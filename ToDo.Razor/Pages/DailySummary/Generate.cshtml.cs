using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.DailySummary;

[Authorize]
public class GenerateModel : PageModel
{
    private readonly PersonalDailySummaryService _service;
    private readonly ILogger<GenerateModel> _logger;

    public GenerateModel(PersonalDailySummaryService service, ILogger<GenerateModel> logger)
    {
        _service = service;
        _logger = logger;
    }

    [TempData]
    public string Message { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(value, out var userId))
        {
            Message = "请先登录";
            return RedirectToPage("Index");
        }

        try
        {
            var result = await _service.GenerateForUserAsync(
                userId,
                AppTime.Today,
                isAutomatic: false,
                forceRebuild: true,
                cancellationToken);
            Message = result.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate personal daily summary for user {UserId}", userId);
            Message = "个人日报生成失败，请稍后重试";
        }

        return RedirectToPage("Index", new { selectDate = AppTime.Today.ToString("yyyy-MM-dd") });
    }
}
