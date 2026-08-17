import { spawn, spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { createInterface } from "node:readline";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { Codex } from "@openai/codex-sdk";

const require = createRequire(import.meta.url);
const root = process.env.SCREEN_GUIDE_2B0_ROOT
  ?? path.join(process.env.LOCALAPPDATA ?? os.tmpdir(), "ScreenGuide", "Experiments", "2B0", "harness-current");
const statePath = path.join(root, "state.json");
const mode = process.argv[2];

await mkdir(root, { recursive: true });

function resolveCodexBinary() {
  if (process.env.CODEX_EXE) return process.env.CODEX_EXE;
  const codexPackage = require.resolve("@openai/codex/package.json");
  const packageRequire = createRequire(codexPackage);
  const platformPackage = process.arch === "arm64"
    ? "@openai/codex-win32-arm64"
    : "@openai/codex-win32-x64";
  const platformPackageJson = packageRequire.resolve(`${platformPackage}/package.json`);
  const triple = process.arch === "arm64" ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
  return path.join(path.dirname(platformPackageJson), "vendor", triple, "bin", "codex.exe");
}

async function makeWorkspace(label) {
  const workspaces = path.join(root, "workspaces");
  await mkdir(workspaces, { recursive: true });
  const workspace = await mkdtemp(path.join(workspaces, `${label}-`));
  const initialized = spawnSync("git", ["init", "--quiet", workspace], { encoding: "utf8" });
  if (initialized.status !== 0) throw new Error(initialized.stderr || "git init failed");
  return workspace;
}

async function saveState(next) {
  let current = {};
  try { current = JSON.parse(await readFile(statePath, "utf8")); } catch { /* first run */ }
  await writeFile(statePath, JSON.stringify({ ...current, ...next }, null, 2), "utf8");
}

async function appendTrace(name, value) {
  const tracePath = path.join(root, `${name}.json`);
  await writeFile(tracePath, JSON.stringify(value, null, 2), "utf8");
}

function output(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

async function sdkStart() {
  const workspace = await makeWorkspace("sdk-start");
  const codex = new Codex();
  const thread = codex.startThread({
    workingDirectory: workspace,
    sandboxMode: "read-only",
    approvalPolicy: "never",
  });
  const { events } = await thread.runStreamed(
    "Remember the marker ORBIT-742 for this conversation. Do not run commands or modify files. Reply exactly BASIC_OK.",
  );
  const seen = [];
  let completedAt = null;
  for await (const event of events) {
    seen.push(event);
    if (event.type === "turn.completed") completedAt = Date.now();
  }
  const processReturnedAt = Date.now();
  if (!thread.id) throw new Error("SDK did not expose a thread id");
  await saveState({ sdkThreadId: thread.id, sdkWorkspace: workspace });
  await appendTrace("sdk-start", seen);
  output({
    probe: "sdk-start",
    threadId: thread.id,
    eventTypes: seen.map((event) => event.type),
    terminalEvent: seen.at(-1)?.type ?? null,
    processReturnedAfterTerminalMs: completedAt === null ? null : processReturnedAt - completedAt,
  });
}

async function sdkResume() {
  const state = JSON.parse(await readFile(statePath, "utf8"));
  const codex = new Codex();
  const thread = codex.resumeThread(state.sdkThreadId, {
    workingDirectory: state.sdkWorkspace,
    sandboxMode: "read-only",
    approvalPolicy: "never",
  });
  const resumed = await thread.run("What marker did I ask you to remember? Reply with only the marker.");
  const structured = await thread.run("Return the verification result.", {
    outputSchema: {
      type: "object",
      properties: {
        status: { type: "string", enum: ["ok"] },
        marker: { type: "string" },
      },
      required: ["status", "marker"],
      additionalProperties: false,
    },
  });
  const parsed = JSON.parse(structured.finalResponse);
  await appendTrace("sdk-resume", {
    threadId: thread.id,
    resumedFinalResponse: resumed.finalResponse,
    structuredFinalResponse: parsed,
  });
  output({
    probe: "sdk-resume",
    sameThreadId: thread.id === state.sdkThreadId,
    rememberedMarker: resumed.finalResponse.trim(),
    structuredOutput: parsed,
  });
}

async function findProcessesByEnvironmentNeedle(needle) {
  const script = [
    "$needle=$env:SG_QUERY_NEEDLE",
    "$rows=Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine.Contains($needle) } | Select-Object ProcessId,ParentProcessId,Name",
    "if ($rows) { $rows | ConvertTo-Json -Compress } else { '[]' }",
  ].join("; ");
  const result = spawnSync("powershell.exe", ["-NoProfile", "-Command", script], {
    encoding: "utf8",
    env: { ...process.env, SG_QUERY_NEEDLE: needle },
  });
  if (result.status !== 0) throw new Error(result.stderr || "process query failed");
  const parsed = JSON.parse(result.stdout.trim() || "[]");
  return Array.isArray(parsed) ? parsed : [parsed];
}

async function sdkCancel() {
  const workspace = await makeWorkspace("sdk-cancel");
  const scriptPath = path.join(workspace, "long-task.ps1");
  await writeFile(scriptPath, "Start-Sleep -Seconds 60\n", "utf8");
  const codex = new Codex();
  const thread = codex.startThread({
    workingDirectory: workspace,
    sandboxMode: "read-only",
    approvalPolicy: "never",
  });
  const controller = new AbortController();
  const { events } = await thread.runStreamed(
    `Run this exact command and wait for it to finish: powershell -NoProfile -File "${scriptPath}". Then reply SLEEP_FINISHED.`,
    { signal: controller.signal },
  );
  const seen = [];
  let aborted = false;
  try {
    for await (const event of events) {
      seen.push(event);
      if (event.type === "item.started" && event.item.type === "command_execution") {
        await new Promise((resolve) => setTimeout(resolve, 800));
        controller.abort();
        aborted = true;
      }
    }
  } catch (error) {
    seen.push({ type: "harness.abort", name: error?.name, message: String(error?.message ?? error) });
  }
  if (!aborted) controller.abort();
  await new Promise((resolve) => setTimeout(resolve, 1500));
  const remaining = await findProcessesByEnvironmentNeedle(scriptPath);
  await appendTrace("sdk-cancel", { events: seen, remaining });
  output({
    probe: "sdk-cancel",
    abortIssued: true,
    eventTypes: seen.map((event) => event.type),
    longTaskProcessesRemaining: remaining.length,
  });
}

class AppServerClient {
  constructor() {
    this.child = null;
    this.nextId = 1;
    this.pending = new Map();
    this.notifications = [];
    this.notificationWaiters = [];
    this.serverRequests = [];
    this.serverRequestWaiters = [];
    this.stderr = "";
  }

  async start() {
    this.child = spawn(resolveCodexBinary(), ["app-server", "--stdio"], {
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    this.child.stderr.on("data", (chunk) => { this.stderr += chunk.toString("utf8"); });
    const lines = createInterface({ input: this.child.stdout });
    lines.on("line", (line) => this.onLine(line));
    await this.request("initialize", {
      clientInfo: { name: "screen-guide-2b0", title: "ScreenGuide 2B-0 Harness", version: "0.1.0" },
      capabilities: { experimentalApi: false, requestAttestation: false },
    });
    this.notify("initialized");
  }

  onLine(line) {
    let message;
    try { message = JSON.parse(line); } catch { return; }
    if (message.id !== undefined && ("result" in message || "error" in message)) {
      const pending = this.pending.get(String(message.id));
      if (pending) {
        this.pending.delete(String(message.id));
        if (message.error) pending.reject(new Error(JSON.stringify(message.error)));
        else pending.resolve(message.result);
      }
      return;
    }
    if (message.id !== undefined && message.method) {
      this.serverRequests.push(message);
      const waiter = this.serverRequestWaiters.shift();
      if (waiter) waiter.resolve(message);
      return;
    }
    if (message.method) {
      this.notifications.push(message);
      for (const waiter of [...this.notificationWaiters]) {
        if (waiter.method === message.method && waiter.predicate(message.params)) {
          clearTimeout(waiter.timer);
          this.notificationWaiters.splice(this.notificationWaiters.indexOf(waiter), 1);
          waiter.resolve(message);
        }
      }
    }
  }

  send(message) {
    this.child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  request(method, params) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(String(id), { resolve, reject });
      this.send({ method, id, params });
    });
  }

  notify(method, params) {
    this.send(params === undefined ? { method } : { method, params });
  }

  respond(id, result) {
    this.send({ id, result });
  }

  waitNotification(method, predicate = () => true, timeoutMs = 90_000) {
    const existing = this.notifications.find((message) => message.method === method && predicate(message.params));
    if (existing) return Promise.resolve(existing);
    return new Promise((resolve, reject) => {
      const waiter = { method, predicate, resolve, reject, timer: null };
      waiter.timer = setTimeout(() => {
        this.notificationWaiters.splice(this.notificationWaiters.indexOf(waiter), 1);
        reject(new Error(`Timed out waiting for ${method}; stderr=${this.stderr.slice(-800)}`));
      }, timeoutMs);
      this.notificationWaiters.push(waiter);
    });
  }

  waitServerRequest(timeoutMs = 90_000) {
    if (this.serverRequests.length > 0) return Promise.resolve(this.serverRequests.shift());
    return new Promise((resolve, reject) => {
      const waiter = { resolve, reject };
      const timer = setTimeout(() => {
        this.serverRequestWaiters.splice(this.serverRequestWaiters.indexOf(waiter), 1);
        reject(new Error(`Timed out waiting for server request; stderr=${this.stderr.slice(-800)}`));
      }, timeoutMs);
      waiter.resolve = (value) => { clearTimeout(timer); resolve(value); };
      this.serverRequestWaiters.push(waiter);
    });
  }

  async stop() {
    if (!this.child || this.child.exitCode !== null) return;
    const exited = new Promise((resolve) => this.child.once("exit", resolve));
    this.child.stdin.end();
    const graceful = await Promise.race([
      exited.then(() => true),
      new Promise((resolve) => setTimeout(() => resolve(false), 3000)),
    ]);
    if (!graceful && this.child.exitCode === null) this.child.kill();
  }
}

async function startThread(client, workspace, overrides = {}) {
  const result = await client.request("thread/start", {
    cwd: workspace,
    approvalPolicy: "never",
    sandbox: "read-only",
    ephemeral: false,
    ...overrides,
  });
  return result.thread;
}

async function startTurn(client, threadId, text, overrides = {}) {
  return client.request("turn/start", {
    threadId,
    input: [{ type: "text", text }],
    ...overrides,
  });
}

async function appServerRestart() {
  const workspace = await makeWorkspace("appserver-restart");
  const first = new AppServerClient();
  await first.start();
  const firstPid = first.child.pid;
  const thread = await startThread(first, workspace);
  const started = await startTurn(first, thread.id, "Remember APP-993. Do not run commands. Reply exactly FIRST_OK.");
  const completed = await first.waitNotification("turn/completed", (params) => params.turn.id === started.turn.id);
  const serverAliveAfterTurn = first.child.exitCode === null;
  await first.stop();

  const second = new AppServerClient();
  await second.start();
  const resumed = await second.request("thread/resume", { threadId: thread.id });
  const secondTurn = await startTurn(second, thread.id, "What marker did I ask you to remember? Reply with only the marker.");
  const secondCompleted = await second.waitNotification("turn/completed", (params) => params.turn.id === secondTurn.turn.id);
  const read = await second.request("thread/read", { threadId: thread.id, includeTurns: true });
  await appendTrace("appserver-restart", {
    firstPid,
    firstThread: thread,
    firstTurn: completed.params.turn,
    resumedThread: resumed.thread,
    secondTurn: secondCompleted.params.turn,
    readThread: read.thread,
  });
  output({
    probe: "appserver-restart",
    firstAppServerPid: firstPid,
    secondAppServerPid: second.child.pid,
    appServerStayedAliveAfterTurn: serverAliveAfterTurn,
    sameThreadId: resumed.thread.id === thread.id,
    sessionId: resumed.thread.sessionId,
    rootThreadIdEqualsSessionId: resumed.thread.sessionId === resumed.thread.id,
    firstTurnStatus: completed.params.turn.status,
    secondTurnStatus: secondCompleted.params.turn.status,
    persistedTurnCount: read.thread.turns.length,
  });
  await second.stop();
}

async function appServerApproval() {
  const workspace = await makeWorkspace("appserver-approval");
  const client = new AppServerClient();
  await client.start();
  const thread = await startThread(client, workspace, {
    approvalPolicy: "untrusted",
    sandbox: "workspace-write",
  });
  const turn = await startTurn(
    client,
    thread.id,
    "Run this exact command once: powershell -NoProfile -Command \"New-Item -ItemType File approval-check.tmp\". Do not use apply_patch. If approval is declined, report DECLINED.",
  );
  const approval = await client.waitServerRequest();
  const waiting = await client.waitNotification(
    "thread/status/changed",
    (params) => params.threadId === thread.id
      && params.status.type === "active"
      && params.status.activeFlags.includes("waitingOnApproval"),
  );
  client.respond(approval.id, { decision: "decline" });
  const resolved = await client.waitNotification("serverRequest/resolved", (params) => String(params.requestId) === String(approval.id));
  const completed = await client.waitNotification("turn/completed", (params) => params.turn.id === turn.turn.id);
  await appendTrace("appserver-approval", { approval, waiting, resolved, completed });
  output({
    probe: "appserver-approval",
    serverRequestMethod: approval.method,
    waitingStatus: waiting.params.status,
    resolvedNotification: resolved.method,
    sameTurnId: completed.params.turn.id === turn.turn.id,
    terminalStatus: completed.params.turn.status,
  });
  await client.stop();
}

async function appServerUserInput() {
  const workspace = await makeWorkspace("appserver-user-input");
  const client = new AppServerClient();
  await client.start();
  const thread = await startThread(client, workspace);
  const turn = await startTurn(
    client,
    thread.id,
    "Before giving any answer, use the request_user_input tool to ask me for a verification color. After I answer, repeat the answer exactly.",
  );
  try {
    const request = await client.waitServerRequest(25_000);
    const waiting = await client.waitNotification(
      "thread/status/changed",
      (params) => params.threadId === thread.id
        && params.status.type === "active"
        && params.status.activeFlags.includes("waitingOnUserInput"),
      25_000,
    );
    const questionId = request.params.questions?.[0]?.id;
    client.respond(request.id, { answers: { [questionId]: { answers: ["BLUE-ANSWER"] } } });
    const completed = await client.waitNotification("turn/completed", (params) => params.turn.id === turn.turn.id);
    await appendTrace("appserver-user-input", { request, waiting, completed });
    output({
      probe: "appserver-user-input",
      available: true,
      serverRequestMethod: request.method,
      waitingStatus: waiting.params.status,
      sameTurnId: completed.params.turn.id === turn.turn.id,
      terminalStatus: completed.params.turn.status,
    });
  } catch (error) {
    const unavailableInDefaultMode = client.stderr.includes("request_user_input is unavailable in Default mode");
    await appendTrace("appserver-user-input", {
      available: false,
      unavailableInDefaultMode,
      error: String(error?.message ?? error),
    });
    output({
      probe: "appserver-user-input",
      available: false,
      unavailableInDefaultMode,
      conclusion: "No user-input server request was emitted in the current Default mode.",
    });
  } finally {
    await client.stop();
  }
}

async function appServerCancel() {
  const workspace = await makeWorkspace("appserver-cancel");
  const scriptPath = path.join(workspace, "long-task.ps1");
  await writeFile(scriptPath, "Start-Sleep -Seconds 60\n", "utf8");
  const client = new AppServerClient();
  await client.start();
  const thread = await startThread(client, workspace, {
    approvalPolicy: "never",
    sandbox: "read-only",
  });
  const turn = await startTurn(
    client,
    thread.id,
    `Run this exact command and wait for it: powershell -NoProfile -File "${scriptPath}". Then reply SLEEP_FINISHED.`,
  );
  await client.waitNotification(
    "item/started",
    (params) => params.turnId === turn.turn.id && params.item?.type === "commandExecution",
  );
  await new Promise((resolve) => setTimeout(resolve, 800));
  const interruptResult = await client.request("turn/interrupt", { threadId: thread.id, turnId: turn.turn.id });
  const completed = await client.waitNotification("turn/completed", (params) => params.turn.id === turn.turn.id);
  await new Promise((resolve) => setTimeout(resolve, 1500));
  const remainingBeforeServerStop = await findProcessesByEnvironmentNeedle(scriptPath);
  const serverAliveAfterInterrupt = client.child.exitCode === null;
  await client.stop();
  await new Promise((resolve) => setTimeout(resolve, 500));
  const remainingAfterServerStop = await findProcessesByEnvironmentNeedle(scriptPath);
  await appendTrace("appserver-cancel", {
    interruptResult,
    completed,
    remainingBeforeServerStop,
    remainingAfterServerStop,
  });
  output({
    probe: "appserver-cancel",
    turnStatus: completed.params.turn.status,
    appServerAliveAfterInterrupt: serverAliveAfterInterrupt,
    longTaskProcessesBeforeServerStop: remainingBeforeServerStop.length,
    longTaskProcessesAfterServerStop: remainingAfterServerStop.length,
  });
}

async function cliFailure() {
  const workspace = await makeWorkspace("cli-failure");
  const child = spawn(resolveCodexBinary(), [
    "exec", "--json", "--sandbox", "read-only", "--config", "approval_policy=\"never\"",
    "--model", "screen-guide-invalid-model", "--cd", workspace,
    "Reply exactly SHOULD_NOT_SUCCEED.",
  ], { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  const events = [];
  let stderr = "";
  const lines = createInterface({ input: child.stdout });
  lines.on("line", (line) => { try { events.push(JSON.parse(line)); } catch { /* ignore non-JSON */ } });
  child.stderr.on("data", (chunk) => { stderr += chunk.toString("utf8"); });
  const exit = await new Promise((resolve) => child.once("exit", (code, signal) => resolve({ code, signal })));
  await appendTrace("cli-failure", { events, exit, stderr });
  output({
    probe: "cli-failure",
    exit,
    eventTypes: events.map((event) => event.type),
    terminalEvent: events.at(-1)?.type ?? null,
    stderrPresent: stderr.length > 0,
  });
}

async function cliHardKillRecovery() {
  const workspace = await makeWorkspace("cli-hard-kill");
  const scriptPath = path.join(workspace, "long-task.ps1");
  await writeFile(scriptPath, "Start-Sleep -Seconds 60\n", "utf8");
  const child = spawn(resolveCodexBinary(), [
    "exec", "--json", "--sandbox", "read-only", "--config", "approval_policy=\"never\"",
    "--cd", workspace,
    `Run this exact command and wait for it: powershell -NoProfile -File "${scriptPath}". Then reply SLEEP_FINISHED.`,
  ], { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  const events = [];
  let stderr = "";
  const startedCommand = new Promise((resolve) => {
    const lines = createInterface({ input: child.stdout });
    lines.on("line", (line) => {
      try {
        const event = JSON.parse(line);
        events.push(event);
        if (event.type === "item.started" && event.item?.type === "command_execution") resolve();
      } catch { /* ignore non-JSON */ }
    });
  });
  child.stderr.on("data", (chunk) => { stderr += chunk.toString("utf8"); });
  let commandStartTimer;
  try {
    await Promise.race([
      startedCommand,
      new Promise((_, reject) => {
        commandStartTimer = setTimeout(() => reject(new Error("command did not start")), 60_000);
      }),
    ]);
  } finally {
    clearTimeout(commandStartTimer);
  }
  await new Promise((resolve) => setTimeout(resolve, 800));
  child.kill("SIGKILL");
  const killedExit = await new Promise((resolve) => child.once("exit", (code, signal) => resolve({ code, signal })));
  await new Promise((resolve) => setTimeout(resolve, 1200));
  const remainingBeforeCleanup = await findProcessesByEnvironmentNeedle(scriptPath);
  for (const processInfo of remainingBeforeCleanup) {
    try { process.kill(Number(processInfo.ProcessId), "SIGKILL"); } catch { /* already exited */ }
  }
  await new Promise((resolve) => setTimeout(resolve, 500));
  const remainingAfterCleanup = await findProcessesByEnvironmentNeedle(scriptPath);
  const threadId = events.find((event) => event.type === "thread.started")?.thread_id;
  if (!threadId) throw new Error("hard-killed run did not emit a thread id");

  const resumedEvents = [];
  let resumedStderr = "";
  const resumed = spawn(resolveCodexBinary(), [
    "exec", "resume", "--json", threadId,
    "The previous turn was interrupted by a host crash. Do not run commands. Reply exactly RECOVERED.",
  ], { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  const resumedLines = createInterface({ input: resumed.stdout });
  resumedLines.on("line", (line) => { try { resumedEvents.push(JSON.parse(line)); } catch { /* ignore */ } });
  resumed.stderr.on("data", (chunk) => { resumedStderr += chunk.toString("utf8"); });
  const resumedExit = await new Promise((resolve) => resumed.once("exit", (code, signal) => resolve({ code, signal })));
  const reader = new AppServerClient();
  await reader.start();
  const read = await reader.request("thread/read", { threadId, includeTurns: true });
  await reader.stop();
  await appendTrace("cli-hard-kill-recovery", {
    events,
    killedExit,
    stderr,
    remainingBeforeCleanup,
    remainingAfterCleanup,
    resumedEvents,
    resumedStderr,
    resumedExit,
    readThread: read.thread,
  });
  output({
    probe: "cli-hard-kill-recovery",
    threadId,
    killedExit,
    terminalEventBeforeKill: events.at(-1)?.type ?? null,
    childProcessesLeftByHardKill: remainingBeforeCleanup.length,
    childProcessesAfterHarnessCleanup: remainingAfterCleanup.length,
    resumedExit,
    resumedEventTypes: resumedEvents.map((event) => event.type),
    resumedTerminalEvent: resumedEvents.at(-1)?.type ?? null,
    persistedTurnStatuses: read.thread.turns.map((turn) => turn.status),
  });
}

const probes = {
  "sdk-start": sdkStart,
  "sdk-resume": sdkResume,
  "sdk-cancel": sdkCancel,
  "appserver-restart": appServerRestart,
  "appserver-approval": appServerApproval,
  "appserver-user-input": appServerUserInput,
  "appserver-cancel": appServerCancel,
  "cli-failure": cliFailure,
  "cli-hard-kill-recovery": cliHardKillRecovery,
};

if (!probes[mode]) {
  process.stderr.write(`Unknown probe: ${mode ?? "<missing>"}\nAvailable: ${Object.keys(probes).join(", ")}\n`);
  process.exitCode = 2;
} else {
  try {
    await probes[mode]();
  } catch (error) {
    process.stderr.write(`${error?.stack ?? error}\n`);
    process.exitCode = 1;
  }
}
