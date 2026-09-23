using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Threading.RateLimiting;
using ToDo.Context;
using ToDo.Domain;
using ToDo.Domain.Options;
using ToDo.Entities;
using ToDo.Razor.Data;

// 启动时验证运行环境能够解析北京时间；业务时间不再依赖服务器系统时区。
_ = AppTime.Now;
// 检查是否设置了腾讯会议Token
//var token = Environment.GetEnvironmentVariable("TENCENT_MEETING_TOKEN");
//if (string.IsNullOrEmpty(token))
//{
//    Console.WriteLine("警告: 未设置环境变量 TENCENT_MEETING_TOKEN，腾讯会议MCP功能将不可用");
//    Console.WriteLine("请在运行前设置: set TENCENT_MEETING_TOKEN=你的Token");
//}
//else
//{
//    Console.WriteLine($"已加载腾讯会议Token: {token.Substring(0, Math.Min(8, token.Length))}...");
//}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

if (builder.Environment.IsProduction()
    && !IntegritySigningService.IsProductionKeyValid(builder.Configuration["Security:IntegritySigning:CurrentKey"]))
    throw new InvalidOperationException("生产环境必须配置至少 32 字节的 Security:IntegritySigning:CurrentKey");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.AddHttpContextAccessor();

var dataProtectionPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtection-Keys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("ToDo.Razor");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var configuredProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
    foreach (var address in configuredProxies)
    {
        if (IPAddress.TryParse(address, out var ipAddress)) options.KnownProxies.Add(ipAddress);
    }
});

builder.Services.AddScoped<ToDo.Domain.AI.IAIService, ToDo.Domain.AI.AIService>();
builder.Services.AddScoped<OperationLogService>();
builder.Services.AddScoped<ProjectDomain>();
builder.Services.AddScoped<ApplicationUserDomainService>();
builder.Services.AddScoped<IMeetingMinutesService, MeetingMinutesService>();
builder.Services.AddScoped<IMeetingAgendaService, MeetingAgendaService>();
builder.Services.AddScoped<IDailyReportService, DailyReportService>();
builder.Services.AddScoped<ToDoTaskDomainService>();
builder.Services.AddScoped<TaskGroupDomainService>();
builder.Services.AddScoped<UserNotificationService>();
builder.Services.AddScoped<MeetingTaskSyncService>();
builder.Services.AddScoped<DistributedLeaseService>();
builder.Services.AddScoped<MeetingActionSupervisionService>();
builder.Services.AddSingleton<AgentQueueSignal>();
builder.Services.AddSingleton<AgentTelemetry>();
builder.Services.AddScoped<AutomationHealthService>();
builder.Services.AddScoped<DataRetentionService>();
builder.Services.AddSingleton<IAgentToolCatalog, AgentToolCatalog>();
builder.Services.AddScoped<IAgentRegistry, AgentRegistry>();
builder.Services.AddScoped<AgentDefinitionSnapshotService>();
builder.Services.AddScoped<AgentAdministrationService>();
builder.Services.AddSingleton<AgentTemplateCatalog>();
builder.Services.AddScoped<AgentTestingService>();
builder.Services.AddScoped<AgentContextService>();
builder.Services.AddScoped<AiSessionService>();
builder.Services.AddScoped<EventBusService>();
builder.Services.AddScoped<IEventBus>(sp => sp.GetRequiredService<EventBusService>());
builder.Services.AddScoped<AgentEventSubscriptionService>();
builder.Services.AddScoped<AgentEventAutomationService>();
builder.Services.AddScoped<AgentExecutionService>();
builder.Services.AddScoped<AgentOutcomeService>();
builder.Services.AddScoped<AgentAcceptanceService>();
builder.Services.Configure<AgentWebSearchOptions>(builder.Configuration.GetSection("AgentWebSearch"));
builder.Services.AddHttpClient<IAgentWebSearch, AgentWebSearchService>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<AgentPlanningService>();
builder.Services.AddScoped<AgentSetupService>();
builder.Services.AddScoped<AgentAutomaticAcceptanceService>();
builder.Services.AddScoped<AgentWorkQueueService>();
builder.Services.AddScoped<AgentDispatchService>();
builder.Services.AddScoped<AgentRunQueueService>();
builder.Services.AddScoped<TaskAssistanceService>();
builder.Services.AddScoped<RedBlueService>();
builder.Services.AddScoped<ScheduledJobService>();
builder.Services.AddScoped<PersonalDailySummaryService>();
builder.Services.AddScoped<ProjectDocumentService>();
builder.Services.AddScoped<ProjectDocumentIndexService>();
builder.Services.AddScoped<DocumentCategoryService>();
builder.Services.AddScoped<ApprovalRequestService>();
builder.Services.AddScoped<AgentDocumentAccessService>();
builder.Services.AddScoped<AgentToolService>();
builder.Services.AddScoped<AgentRiskPolicyService>();
builder.Services.Configure<IntegritySigningOptions>(builder.Configuration.GetSection("Security:IntegritySigning"));
builder.Services.AddSingleton<IntegritySigningService>();
builder.Services.Configure<DataRetentionOptions>(builder.Configuration.GetSection("DataRetention"));
builder.Services.Configure<AgentAutomationOptions>(
    builder.Configuration.GetSection("AgentAutomation"));
builder.Services.AddHostedService<EventBusDispatcher>();
builder.Services.AddHostedService<ScheduledTaskHostedService>();
builder.Services.AddHostedService<AgentAutomationHostedService>();
builder.Services.AddHostedService<ProjectDocumentIndexHostedService>();
builder.Services.AddHostedService<MeetingActionSupervisionHostedService>();
builder.Services.AddHostedService<DataRetentionHostedService>();
//// 绑定腾讯会议CLI配置
//builder.Services.Configure<ToDo.Domain.Options.MeetingCliOptions>(
//    builder.Configuration.GetSection("MeetingConfig"));
//builder.Services.AddScoped<ITencentMeetingLocalCliService, TencentMeetingLocalCliService>();
// 绑定两套配置
builder.Services.Configure<ToDo.Domain.Options.MeetingCliOptions>(
    builder.Configuration.GetSection("MeetingConfig"));
builder.Services.Configure<ToDo.Domain.Options.WeComOptions>(
    builder.Configuration.GetSection("Integrations:WeCom"));
builder.Services.Configure<ToDo.Domain.Options.TencentMeetingOptions>(
    builder.Configuration.GetSection("Integrations:TencentMeeting"));

// HttpClient 供MCP服务使用
// 先注册HttpClient（保持原有）
builder.Services.AddHttpClient<TencentMeetingMcpService>();
builder.Services.AddHttpClient<IWeComGateway, WeComGateway>();
builder.Services.AddHttpClient<ITencentMeetingGateway, TencentMeetingGateway>();

// 【修复后的动态选择服务】
builder.Services.AddScoped<ITencentMeetingLocalCliService>(sp =>
{
    var opt = sp.GetRequiredService<IOptions<TencentMeetingOptions>>().Value;
    if (opt.UseLocalCliFetch)
    {
        // tmeet 本地CLI模式
        return new TencentMeetingLocalCliService(
            sp.GetRequiredService<IOptions<MeetingCliOptions>>(),
            sp.GetRequiredService<ILogger<TencentMeetingLocalCliService>>());
    }
    else
    {
        // 龙虾MCP模式，直接从DI获取已经注册好的HttpClient
        var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TencentMeetingMcpService));
        return new TencentMeetingMcpService(
            httpClient,
            sp.GetRequiredService<IOptions<TencentMeetingOptions>>(),
            sp.GetRequiredService<ILogger<TencentMeetingMcpService>>());
    }
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString)
    || (builder.Environment.IsProduction()
        && connectionString.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException(
        "未配置有效的数据库连接字符串。请通过环境变量 ConnectionStrings__DefaultConnection 配置生产数据库。");
}
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 30)), mysql =>
    {
        mysql.MigrationsAssembly("ToDo.Context");
        mysql.EnableStringComparisonTranslations();
    }));

builder.Services.AddIdentity<ApplicationUser, IdentityRole<int>>(options =>
{
    // 只影响新建或重置的密码，不会使现有账号立即失效。
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 10;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequiredUniqueChars = 4;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
})
.AddRoles<IdentityRole<int>>()
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/Account/Login", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("non-login");

        var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"login:{clientKey}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.ConfigureFilter(new ToDo.Razor.Filters.ProjectReadOnlyExceptionFilter());
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Lockout");
});

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.UseDeveloperExceptionPage();
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    var cspNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
    context.Items["CspNonce"] = cspNonce;
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
        headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        headers.Append(
            "Content-Security-Policy",
            "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; " +
            "form-action 'self'; img-src 'self' data: https:; " +
            $"style-src 'self' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; style-src-elem 'self' 'nonce-{cspNonce}' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; style-src-attr 'unsafe-inline'; " +
            $"script-src 'self' https://cdn.jsdelivr.net; script-src-elem 'self' 'nonce-{cspNonce}' https://cdn.jsdelivr.net; script-src-attr 'unsafe-inline'; " +
            "font-src 'self' data: https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; connect-src 'self'");
        return Task.CompletedTask;
    });
    await next();
});
app.Use(async (context, next) =>
{
    // 日报附件必须通过带项目权限校验的 Razor Page 下载处理器访问。
    if (context.Request.Path.StartsWithSegments("/uploads/reports", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/uploads/meeting", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/uploads/projects", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapPost("/api/projects/{projectId}/members", async (
    int projectId,
    [FromBody] AddMemberRequest request,
    [FromServices] ProjectDomain projectDomain,
    ClaimsPrincipal user) =>
{
    if (!int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId) || currentUserId <= 0)
        return Results.Unauthorized();
    if (!Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), out var currentRole))
        return Results.Forbid();

    var result = await projectDomain.AddProjectMemberAsync(projectId, request.UserId, currentUserId, currentRole, request.MakeAdmin);
    return result ? Results.Ok() : Results.BadRequest();
}).RequireAuthorization();

app.MapGet("/api/users/search", async ([FromQuery] string? q, [FromServices] ApplicationDbContext context) =>
{
    var keyword = q?.Trim() ?? string.Empty;
    var users = await context.Users
        .Where(user => !user.IsDeleted
            && user.Status == UserStatus.Active
            && (keyword == string.Empty
                || user.UserName!.Contains(keyword)
                || (user.RealName != null && user.RealName.Contains(keyword))))
        .Select(u => new { u.Id, u.UserName, u.RealName })
        .Take(10)
        .ToListAsync();
    return Results.Ok(users);
}).RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.systemAdmin)));

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        await SeedData.Initialize(services);
        await services.GetRequiredService<IAgentRegistry>().EnsureDefaultsAsync();
        var adminUser = await services.GetRequiredService<UserManager<ApplicationUser>>().Users
            .FirstOrDefaultAsync(u => u.Role == UserRole.systemAdmin && u.Status == UserStatus.Active);
        if (adminUser != null) await services.GetRequiredService<ScheduledJobService>().EnsureDefaultAsync(adminUser.Id);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"初始化数据库和种子数据失败：{ex.Message}");
        throw;
    }
}

app.MapRazorPages();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (AutomationHealthService health, CancellationToken cancellationToken) =>
{
    var snapshot = await health.GetAsync(cancellationToken);
    return Results.Json(
        new { snapshot.Status, snapshot.CheckedAt },
        statusCode: snapshot.DatabaseAvailable ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
app.MapGet("/", () => Results.Redirect("/ActionCenter"));
app.Run();

public record AddMemberRequest(int UserId, bool MakeAdmin);
