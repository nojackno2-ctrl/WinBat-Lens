# Project State & Handoff

## Current Objective
Build a fully self-contained portable single-file executable (`--self-contained true`, embedding .NET 8 runtime so users need zero pre-installed runtimes) and upload/push to GitHub.

## Project Status
- User requested: "重新用一個正式版單一免安裝執行檔幫我上傳到github" (Re-build a 100% standalone self-contained portable release executable and upload to GitHub).

## Next Steps
1. Execute `dotnet publish`:
   - Command: `dotnet publish WinBatLens.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish/`
2. Verify `publish/WinBatLens.exe` standalone binary.
3. Commit and push all updates to Git & GitHub (`git push origin main`).
4. Update `AI_HANDOFF.md` and `walkthrough.md`.
