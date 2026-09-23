using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net;
using ToDo.Domain;
using ToDo.Entities;

namespace ToDo.Razor.Pages.MeetingMinutes;

[Authorize]
public class VersionListModel : PageModel
{
    private readonly IMeetingMinutesService _meetingService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IWebHostEnvironment _webEnv;

    public VersionListModel(
        IMeetingMinutesService meetingService,
        UserManager<ApplicationUser> userManager,
        IWebHostEnvironment webEnv)
    {
        _meetingService = meetingService;
        _userManager = userManager;
        _webEnv = webEnv;
    }

    [BindProperty(SupportsGet = true)]
    public int MeetingId { get; set; }

    public List<MeetingVersion> VersionList { get; set; } = new();
    public Dictionary<int, string> VersionDiffSummaryDict { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();

        VersionList = await _meetingService.GetMeetingVersionListAsync(MeetingId, currentUser);
        VersionDiffSummaryDict = VersionList.ToDictionary(
            version => version.Id,
            version => version.VersionNumber == 1
                ? "初始版本，无变更"
                : "点击“查看版本差异”后按需生成摘要");

        return Page();
    }

    /// <summary>按需加载当前版本与上一版本的差异。</summary>
    public async Task<JsonResult> OnPostLoadDiffAsync(int newVerId)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null)
            return new JsonResult(new { success = false, msg = "请先登录" }) { StatusCode = 401 };

        var allVersions = await _meetingService.GetMeetingVersionListAsync(MeetingId, currentUser);
        var newVersion = allVersions.FirstOrDefault(version => version.Id == newVerId);
        if (newVersion == null)
            return new JsonResult(new { success = false, msg = "版本不存在或无权访问" }) { StatusCode = 404 };

        var oldVersion = allVersions.FirstOrDefault(version =>
            version.VersionNumber == newVersion.VersionNumber - 1);
        if (oldVersion == null)
            return new JsonResult(new { success = false, msg = "找不到上一版本" });

        var diffData = await _meetingService.CompareTwoVersionAsync(oldVersion.Id, newVersion.Id, currentUser);
        var (oldDiffHtml, newDiffHtml) = DiffHelper.GetTwoDiffHtml(diffData.OldContent, diffData.NewContent);

        return new JsonResult(new
        {
            success = true,
            oldContent = diffData.OldContent,
            newContent = diffData.NewContent,
            aiSummary = diffData.AiDiffSummary,
            oldVersionDbId = oldVersion.Id,
            oldDiffHtml,
            newDiffHtml
        });
    }

    public async Task<IActionResult> OnPostRollbackAsync(int targetVersionId)
    {
        var currentUser = await _userManager.GetUserAsync(User);
        if (currentUser == null) return Challenge();

        var result = await _meetingService.RollbackToVersionAsync(
            MeetingId,
            targetVersionId,
            currentUser,
            _webEnv.WebRootPath);

        TempData[result.Success ? "SuccessMsg" : "ErrorMsg"] = result.Msg;
        return RedirectToPage(new { meetingId = MeetingId });
    }

    public Task<IActionResult> OnPostRollbackFromDiffAsync(int diffRollbackVerId) =>
        OnPostRollbackAsync(diffRollbackVerId);

    private static class DiffHelper
    {
        /// <summary>
        /// Highlights the changed middle section after removing the shared prefix
        /// and suffix. This avoids the unbounded O(n²) memory use of the old LCS.
        /// </summary>
        public static (string OldDiffHtml, string NewDiffHtml) GetTwoDiffHtml(string? oldText, string? newText)
        {
            oldText ??= string.Empty;
            newText ??= string.Empty;

            var prefixLength = 0;
            var prefixLimit = Math.Min(oldText.Length, newText.Length);
            while (prefixLength < prefixLimit && oldText[prefixLength] == newText[prefixLength])
                prefixLength++;

            var suffixLength = 0;
            while (suffixLength < oldText.Length - prefixLength
                && suffixLength < newText.Length - prefixLength
                && oldText[oldText.Length - 1 - suffixLength] == newText[newText.Length - 1 - suffixLength])
            {
                suffixLength++;
            }

            var oldMiddleLength = oldText.Length - prefixLength - suffixLength;
            var newMiddleLength = newText.Length - prefixLength - suffixLength;
            var prefix = Encode(oldText[..prefixLength]);
            var suffix = suffixLength == 0 ? string.Empty : Encode(oldText[^suffixLength..]);
            var oldMiddle = Encode(oldText.Substring(prefixLength, oldMiddleLength));
            var newMiddle = Encode(newText.Substring(prefixLength, newMiddleLength));

            var oldHtml = prefix
                + (oldMiddle.Length == 0 ? string.Empty : $"<del class=\"diff-del\">{oldMiddle}</del>")
                + suffix;
            var newHtml = prefix
                + (newMiddle.Length == 0 ? string.Empty : $"<ins class=\"diff-add\">{newMiddle}</ins>")
                + suffix;
            return (oldHtml, newHtml);
        }

        private static string Encode(string value) => WebUtility.HtmlEncode(value);
    }
}
