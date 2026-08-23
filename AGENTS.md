# Project instructions for coding assistants

## Product boundary

Build a Windows learning assistant that follows the foreground app at question time and gives step-by-step Chinese guidance. The guarded action phase may execute a small allowlist of explicit user voice commands: open a known application or website, or fill and submit a reliably identified search box. It must not move the pointer or invoke destructive actions.

## Privacy and security

- Never add real API keys, tokens, cookies, credentials, screenshots, recordings, personal logs, or machine-specific user data to the repository.
- Store runtime data outside the repository under the current user's local application-data directory.
- Require visible, explicit user consent before starting any capture session.
- Capture only the single foreground window that the user is actively asking about, never the entire desktop. Require explicit consent for the current application run before the first capture.
- Do not implement hidden startup, covert capture, persistence, privilege escalation, credential collection, or remote unauthenticated control.
- Treat each explicit action phrase as authorization for only that single action. Never infer permission for later actions from ordinary conversation.
- Never type into an unidentified field. Sending, publishing, paying, deleting, changing account/security settings, or entering passwords always requires a separate visible confirmation and is outside the current action allowlist.
- Use mock images and fake credentials in tests and documentation.

## Engineering rules

- Prefer Windows UI Automation for structured controls and Windows Graphics Capture for visual context.
- Keep model providers behind an interface; do not couple the application to one vendor.
- Keep capture, analysis, guidance, storage, and UI as separate components.
- Route every new home-page chat, guarded desktop action, single-window observation, and coding request through the DesktopHost `SessionCoordinator`. Do not add a second “current task” or “current conversation” state in DesktopClient or a feature page.
- Treat a Session as one explicit topic and a Turn as one user request. Continue the current Session by default; create a new Session only from an explicit user action, not from an automatic timeout guess.
- Keep Conversation/provider history, Session/Turn state, coding Task state, and future long-term memory as separate concepts.
- Preserve one original Turn while supplying a missing project, file, window, consent, or confirmation. Re-plan after context changes and never reuse consent for a changed target.
- Preserve structured target identifiers for UI-selected actions end to end. Application actions bind the discovered application ID; website actions bind the normalized full HTTPS URI. Persist expected and planned targets and compare them exactly in Host planning and confirmation; cancel on any mismatch.
- A user stop or replacement input must propagate cancellation to the real provider/process, window operation, or coding Task. Cancellation and success must arbitrate one persisted terminal state; a cancelled Turn must reject late success and assistant output.
- On restart, restore the current Session, interrupt unsafe in-flight work, and never replay it automatically. Waiting context may be preserved only when resuming cannot itself execute an action.
- Session coordination never grants authority. CapabilityPolicyEngine, authorized project scope, explicit file confirmation, and per-window visible consent remain the security boundary.
- Add tests for permission boundaries and ensure capture stops immediately when the user selects Stop.
- Add conflict, idempotency, restart, late-result, rapid-input, context-continuation, IPC-pressure, and real Release UI tests for material Session changes.
- Do not claim a capability works until it has been exercised in a real low-risk test.

## Stage 1 scope boundary

- V0.3.0 / V2 Stage 1 implements unified Session coordination, continuous conversation, true cancellation, context completion, unified UI state, and incremental local status updates.
- Do not add DeepSeek, replace the normal chat model, implement long-term memory, RAG, a vector database, complex multi-agent product features, mobile support, or broader Tool Calling as part of Stage 1 maintenance.
- Do not weaken V0.2.1 action, project, file, screen-capture, privacy, or confirmation gates for conversational convenience.
- Protocol v7 and SQLite schema v7 are current as-built contracts. Session snapshots use coordinator instance identity, start time, and change version to reject stale pre-restart updates. Any later protocol/schema change requires migration, compatibility, rollback, and real-client tests.

## Git rules

- Review staged changes before every commit.
- Keep generated files, local configuration, logs, recordings, and captures ignored.
- Do not add an open-source license until the owner explicitly chooses one.
- Keep `v0.2.1-baseline` reachable. Never use a destructive reset or cleanup to perform a rollback; use a clean export/worktree and preserve the current database.
- V0.2.1 cannot open the V0.3.0 schema-v7 database. A program rollback must use the automatic pre-v7 database backup or an isolated data directory.
- Do not run a same-AppId installer/uninstaller lifecycle over an installed version that must be preserved; use a clean machine or isolated Windows environment so uninstall registration and user data are not overwritten.
- Freeze a release in two commits when the baseline records its own artifact hash: tag the complete source/document commit first, rebuild and hash from that tag, then record the tag commit and artifact hash in a documentation-only evidence commit.

## Project source of truth

- `PRODUCT.md` records implemented product capability and known gaps.
- `ARCHITECTURE.md` records the current as-built system only.
- `MEMORY.md` records durable project decisions and current stage state.
- `CHANGELOG.md` records released changes; `ROADMAP.md` records future plans.
- `docs/baselines/` records exact version, tests, installer hashes, and freeze evidence.
- Historical V0.1/V0.2 reports remain evidence but must not override the files above.
- A version is frozen only when its baseline tag exists, locked clean-source build and tests pass, installer acceptance passes, and the working tree is clean.
- The Stage 1 completion report is `docs/baselines/YUANSHU_V2_STAGE1_COMPLETION_REPORT.md`; the exact V0.3.0 identity belongs in `docs/baselines/V0.3.0_STAGE1.md`.
