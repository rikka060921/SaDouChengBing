using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.Integrations;

[Authorize(Roles = "systemAdmin")]
public class IndexModel : PageModel
{
    private readonly IWeComGateway _weCom;
    private readonly ITencentMeetingGateway _tencent;
    private readonly ApplicationDbContext _context;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IWeComGateway weCom, ITencentMeetingGateway tencent, ApplicationDbContext context, ILogger<IndexModel> logger)
    {
        _weCom = weCom;
        _tencent = tencent;
        _context = context;
        _logger = logger;
    }

    public List<IntegrationCallRecord> Calls { get; set; } = new();
    public string ResultMessage { get; set; } = string.Empty;
    public string? LoadError { get; set; }

    [BindProperty] public string Recipient { get; set; } = string.Empty;
    [BindProperty] public string MessageContent { get; set; } = string.Empty;
    [BindProperty] public string MeetingSubject { get; set; } = string.Empty;

    public async Task OnGetAsync() => await SafeLoadAsync();

    public async Task<IActionResult> OnPostWeComAsync()
    {
        if (string.IsNullOrWhiteSpace(Recipient) || string.IsNullOrWhiteSpace(MessageContent))
            ModelState.AddModelError(string.Empty, "请填写接收对象和消息内容");
        else
        {
            try
            {
                var result = await _weCom.SendTextAsync(Recipient.Trim(), MessageContent.Trim());
                ResultMessage = result.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WeCom integration test failed");
                ResultMessage = "企业微信接口调用失败，请检查服务器配置与网络";
            }
        }

        await SafeLoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostTencentAsync()
    {
        if (string.IsNullOrWhiteSpace(MeetingSubject))
            ModelState.AddModelError(nameof(MeetingSubject), "请输入会议主题");
        else
        {
            try
            {
                var result = await _tencent.CreateMeetingAsync(MeetingSubject.Trim(), AppTime.Now.AddHours(1), AppTime.Now.AddHours(2));
                ResultMessage = result.Message;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tencent Meeting integration test failed");
                ResultMessage = "腾讯会议接口调用失败，请检查服务器配置与网络";
            }
        }

        await SafeLoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Calls = await _context.IntegrationCallRecords.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync();
    }

    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load integration call records");
            Calls = [];
            LoadError = "调用记录暂时无法加载；接口测试功能仍可使用";
        }
    }
}
