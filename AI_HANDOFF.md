# Project State & Handoff

## Current Objective
Re-create fresh Git tag `v1.0.0` and publish GitHub Release `v1.0.0` with `publish/WinBatLens.exe` (74.3 MB 100% self-contained single-file executable) explicitly attached under Assets.

## Project Status
- User deleted all tags and releases on GitHub, requesting: "我把releases跟tag全部刪掉了，你重新上傳一次，我要完整打包進執行檔的" (Deleted all releases and tags, re-upload from scratch!).

## Next Steps
1. Re-build `./publish/WinBatLens.exe` via `dotnet publish WinBatLens.csproj -c Release -o ./publish/`.
2. Delete local tag `v1.0.0` and create fresh tag `v1.0.0`.
3. Push commits and tag to GitHub (`git push origin main`, `git push origin v1.0.0 --force`).
4. Publish GitHub Release via `gh release create v1.0.0 ./publish/WinBatLens.exe --title "⚡ WinBat Lens v1.0.0 - Official Release" --notes-file release_notes.md`.
5. Verify `gh release view v1.0.0` shows `WinBatLens.exe` under Assets.
6. Update `AI_HANDOFF.md` and `walkthrough.md`.
