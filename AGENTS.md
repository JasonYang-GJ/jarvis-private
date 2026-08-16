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
- Add tests for permission boundaries and ensure capture stops immediately when the user selects Stop.
- Do not claim a capability works until it has been exercised in a real low-risk test.

## Git rules

- Review staged changes before every commit.
- Keep generated files, local configuration, logs, recordings, and captures ignored.
- Do not add an open-source license until the owner explicitly chooses one.
