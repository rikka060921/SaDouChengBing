using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ToDo.Razor.Pages.Account;

[AllowAnonymous]
public sealed class LockoutModel : PageModel
{
    public void OnGet()
    {
    }
}
