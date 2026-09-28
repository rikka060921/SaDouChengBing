# Working agreement

## Communication
- Respond in Chinese. Clarify material ambiguity before implementation; do not repeatedly ask for choices already settled.
- For longer tasks, report meaningful progress and verification boundaries.

## One development checkout
- On the owner's Windows PC, edit, build and run ONLY in `D:\0.C#\Work miracles(SaDouChengBing)`.
- Open `ToDo.sln` from that directory in Visual Studio.
- `artifacts/github-export-20260923` is a public publication mirror, not a development directory.
- `artifacts/team-sync-20260923` is an old integration worktree, not a development directory.
- Do not create another development copy to avoid local uncommitted changes. Inspect status, preserve work, and clarify conflicts.
- Other developers' clones can use their own paths. These local mirror paths are not application dependencies.

## Standing delivery instruction from owner (2026-09-28)
- After requested implementation is reviewed and verified, explicitly stage only relevant files, write a detailed commit, and synchronize the primary checkout with BOTH remotes using `deploy/Sync-ToDoRepositories.ps1`.
- This is delivery-time automation, NOT file-save, timer, or blind background publication.
- If the user says do not commit/push, that current request takes precedence. Analysis-only tasks do not authorize commits.
- Default sync runs Release tests and Release + Debug builds. Never claim success before checking both remote tips.
- Do not push team history to public GitHub. The public mirror keeps its own history and excludes runtime data and raw meeting caches.
- Review diffs for secrets and private material before committing. Filename filtering is not a complete secret scanner.
- Never force-push, reset working trees, auto-stage all files, overwrite remote changes, or deploy as part of synchronization.
- Two remotes cannot be pushed atomically. Report partial success clearly, preserve the prepared commit, and retry without changing source HEAD.
- See `docs/本地开发与双仓库同步.md` for configuration, recovery and limitations.
