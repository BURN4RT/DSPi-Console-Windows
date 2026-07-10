# DSPiConsole-Windows — Working Agreement

## Git
- **Never commit without being explicitly asked to do so.** Make and build changes,
  but leave them uncommitted until the user explicitly requests a commit. "Proceed",
  "continue", or approving a change is NOT a request to commit.

## Build
- Build with `dotnet build -p:Platform=x64` from the repo root. Do NOT use the
  `AnyCPU` default (fails: WindowsAppSDK needs an explicit RID) and do NOT target
  ARM64 — this project is x86_64 only.
- A full Release build fails to copy output DLLs while the app is running (file
  lock, not a compile error). To verify a compile without closing the app, build a
  single project (e.g. `dotnet build DSPiConsole.Usb/DSPiConsole.Usb.csproj -p:Platform=x64`).
