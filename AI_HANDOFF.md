# Project State & Handoff

## Current Objective
Build a formal production release package (`WinBatLens.exe`) using `dotnet publish -c Release` and commit/push all updates to Git & GitHub.

## Project Status
- User requested: "更新到GitHUB並發布一樣正式版的執行檔" (Push to GitHub and release a formal production build executable).

## Next Steps
1. Execute production build via `dotnet publish`:
   - Command: `dotnet publish WinBatLens.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish/`
2. Inspect `./publish/` output directory and verify `WinBatLens.exe` binary.
3. Commit any uncommitted changes to Git (`git commit`).
4. Update `AI_HANDOFF.md` and `walkthrough.md` with release links and instructions.
