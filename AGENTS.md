# Collaboration Guidelines

## Repository Overview
WinBat Lens is a GUI application / dashboard designed to generate, parse, analyze, and display Windows Battery Reports (`powercfg /batteryreport`) in a user-friendly format.

## AI Agent Rules
- Always update `AI_HANDOFF.md` upon completing major tasks or milestones.
- Keep track of git status and uncommitted changes.
- Ensure all features, UI elements, and system interactions are tested and verified.

## Automatic commits and push to GitHub (user authorization, 2026-10-14)

- The user has authorized automatic local commits and automatic push to GitHub for all projects.
- After completing a task and appropriate verification, commit the task changes and push to GitHub (`origin`) without asking for confirmation again; do not create empty commits.
- Review the diff and preserve existing work. Include unrelated pre-existing changes only when the user explicitly requests committing them. Never commit secrets, credentials, or personal runtime data.
- This standing authorization covers local commits and pushing verified work to GitHub. Destructive operations (force-push, release, merge, rebase, reset, branch deletion) still require explicit authorization.
- A Git `post-commit` hook is configured in `.git/hooks/post-commit` to automatically push the current branch to `origin` upon commit.
- Record what was verified and any unverified behavior in `AI_HANDOFF.md`; never present a commit as proof that functionality works.
