# Codex Integration Harness

This directory contains disposable probes for V0.1 stage 2B-0. It is deliberately
isolated from `ScreenGuide.Core`, Tasking, Persistence, and Desktop Host.

Runtime workspaces, protocol traces, and state are written outside the repository
under `%LOCALAPPDATA%\ScreenGuide\Experiments\2B0`. The harness never reads or
copies Codex credentials.

Install and run a probe:

```powershell
npm install
npm run probe -- sdk-start
npm run probe -- sdk-resume
npm run probe -- sdk-cancel
npm run probe -- appserver-restart
npm run probe -- appserver-approval
npm run probe -- appserver-user-input
npm run probe -- appserver-cancel
npm run probe -- cli-failure
npm run probe -- cli-hard-kill-recovery
```

Set `SCREEN_GUIDE_2B0_ROOT` to reuse one runtime folder across separate invocations.
Only summaries are printed; raw protocol events stay in that runtime folder.
