using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using ToDo.Domain;
using ToDo.Domain.Options;

namespace ToDo.Razor.Pages.SystemHealth;

[Authorize(Roles = "systemAdmin")]
public sealed class IndexModel : PageModel
{
    private readonly AutomationHealthService _health;
    private readonly IntegritySigningService _signing;
    private readonly DataRetentionOptions _retention;

    public IndexModel(
        AutomationHealthService health,
        IntegritySigningService signing,
        IOptions<DataRetentionOptions> retention)
    {
        _health = health;
        _signing = signing;
        _retention = retention.Value;
    }

    public AutomationHealthSnapshot Snapshot { get; private set; } = null!;
    public bool SigningConfigured => _signing.IsConfigured;
    public string SigningKeyId => _signing.CurrentKeyId;
    public bool RetentionEnabled => _retention.Enabled;
    public string TelemetrySource => AgentTelemetry.SourceName;

    public async Task OnGetAsync()
    {
        Snapshot = await _health.GetAsync(HttpContext.RequestAborted);
    }
}
