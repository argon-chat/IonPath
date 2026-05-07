import * as path from "path";
import * as net from "net";
import * as cp from "child_process";
import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  StreamInfo,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;
let serverProcess: cp.ChildProcess | undefined;
let statusBarItem: vscode.StatusBarItem;
let outputChannel: vscode.OutputChannel;

const MIN_IONC_VERSION = "1.4.0";

// ─── Prerequisite Checks ────────────────────────────────────────────────────

async function checkDotnet(): Promise<boolean> {
  try {
    const result = cp.execSync("dotnet --version", { encoding: "utf8", timeout: 10000 });
    outputChannel.appendLine(`[ionc] .NET SDK: ${result.trim()}`);
    return true;
  } catch {
    return false;
  }
}

function findIonc(context: vscode.ExtensionContext): string | null {
  const config = vscode.workspace.getConfiguration("ionpath");
  const configured = config.get<string>("compilerPath", "");

  if (configured && configured !== "ionc") {
    return configured;
  }

  // Dev mode: use locally-built ionc
  if (process.env.IONPATH_DEV === "1") {
    const ext = process.platform === "win32" ? ".exe" : "";
    const devPath = path.resolve(
      context.extensionPath, "..", "..", "src", "ionc", "bin", "Debug", "net10.0", `ionc${ext}`
    );
    try {
      require("fs").accessSync(devPath);
      return devPath;
    } catch {
      return null;
    }
  }

  // Check if ionc is in PATH
  try {
    const cmd = process.platform === "win32" ? "where ionc" : "which ionc";
    const result = cp.execSync(cmd, { encoding: "utf8", timeout: 5000 }).trim();
    return result.split(/\r?\n/)[0];
  } catch {
    return null;
  }
}

function getIoncVersion(ioncPath: string): string | null {
  try {
    // ionc serve outputs version in stderr, or we can check the assembly
    const result = cp.execSync(`"${ioncPath}" --version`, { encoding: "utf8", timeout: 5000, stdio: ["pipe", "pipe", "pipe"] });
    return result.trim();
  } catch {
    // ionc doesn't support --version yet, assume OK
    return null;
  }
}

function compareVersions(a: string, b: string): number {
  const pa = a.split(".").map(Number);
  const pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i++) {
    const diff = (pa[i] || 0) - (pb[i] || 0);
    if (diff !== 0) return diff;
  }
  return 0;
}

async function ensurePrerequisites(context: vscode.ExtensionContext): Promise<string | null> {
  // 1. Check .NET
  const hasDotnet = await checkDotnet();
  if (!hasDotnet) {
    const action = await vscode.window.showErrorMessage(
      "IonPath requires .NET 10 SDK or later. Please install it.",
      "Download .NET",
      "Dismiss"
    );
    if (action === "Download .NET") {
      vscode.env.openExternal(vscode.Uri.parse("https://dotnet.microsoft.com/download"));
    }
    return null;
  }

  // 2. Check ionc
  const ioncPath = findIonc(context);
  if (!ioncPath) {
    const action = await vscode.window.showErrorMessage(
      "IonPath compiler (ionc) not found. Install it as a .NET global tool.",
      "Install ionc",
      "Configure Path",
      "Dismiss"
    );
    if (action === "Install ionc") {
      const terminal = vscode.window.createTerminal("Install ionc");
      terminal.show();
      terminal.sendText("dotnet tool install -g ionc");
    } else if (action === "Configure Path") {
      vscode.commands.executeCommand("workbench.action.openSettings", "ionpath.compilerPath");
    }
    return null;
  }

  // 3. Check version (if supported)
  const version = getIoncVersion(ioncPath);
  if (version && compareVersions(version, MIN_IONC_VERSION) < 0) {
    const action = await vscode.window.showWarningMessage(
      `IonPath: ionc version ${version} is outdated. Minimum required: ${MIN_IONC_VERSION}.`,
      "Update ionc",
      "Continue Anyway"
    );
    if (action === "Update ionc") {
      const terminal = vscode.window.createTerminal("Update ionc");
      terminal.show();
      terminal.sendText("dotnet tool update -g ionc");
      return null;
    }
    if (!action || action !== "Continue Anyway") {
      return null;
    }
  }

  outputChannel.appendLine(`Using ionc: ${ioncPath}`);
  return ioncPath;
}

// ─── LSP Server ─────────────────────────────────────────────────────────────

function spawnServerAndConnect(
  compilerPath: string
): Promise<StreamInfo> {
  return new Promise((resolve, reject) => {
    const proc = cp.spawn(compilerPath, ["serve"], {
      stdio: ["pipe", "pipe", "pipe"],
    });

    serverProcess = proc;
    let portFound = false;

    proc.stderr!.on("data", (data: Buffer) => {
      const text = data.toString();
      outputChannel.appendLine(`[ionc stderr] ${text.trim()}`);

      if (!portFound) {
        const match = text.match(/IONC_LSP_PORT=(\d+)/);
        if (match) {
          portFound = true;
          const port = parseInt(match[1], 10);
          outputChannel.appendLine(`Connecting to LSP on port ${port}`);

          const socket = net.connect({ port, host: "127.0.0.1" }, () => {
            resolve({ reader: socket, writer: socket });
          });
          socket.on("error", (err) => reject(err));
        }
      }
    });

    proc.stdout!.on("data", (data: Buffer) => {
      outputChannel.appendLine(`[ionc stdout] ${data.toString().trim()}`);
    });

    proc.on("error", (err) => {
      reject(new Error(`Failed to start ionc: ${err.message}`));
    });

    proc.on("exit", (code) => {
      if (!portFound) {
        reject(new Error(`ionc exited with code ${code} before LSP started`));
      }
    });

    setTimeout(() => {
      if (!portFound) {
        proc.kill();
        reject(new Error("Timeout: ionc did not report LSP port within 15s"));
      }
    }, 15000);
  });
}

// ─── Status Bar ─────────────────────────────────────────────────────────────

function updateStatusBar(state: "starting" | "running" | "error" | "stopped", detail?: string) {
  switch (state) {
    case "starting":
      statusBarItem.text = "$(loading~spin) IonPath";
      statusBarItem.tooltip = "IonPath: Starting language server...";
      statusBarItem.backgroundColor = undefined;
      break;
    case "running":
      statusBarItem.text = "$(check) IonPath";
      statusBarItem.tooltip = detail ? `IonPath: ${detail}` : "IonPath: Language server running";
      statusBarItem.backgroundColor = undefined;
      break;
    case "error":
      statusBarItem.text = "$(error) IonPath";
      statusBarItem.tooltip = detail ? `IonPath: ${detail}` : "IonPath: Error";
      statusBarItem.backgroundColor = new vscode.ThemeColor("statusBarItem.errorBackground");
      break;
    case "stopped":
      statusBarItem.text = "$(circle-slash) IonPath";
      statusBarItem.tooltip = "IonPath: Server not running";
      statusBarItem.backgroundColor = undefined;
      break;
  }
  statusBarItem.show();
}

// ─── TreeView Provider ──────────────────────────────────────────────────────

type SchemaNodeKind = "msg" | "service" | "enum" | "flags" | "union" | "typedef" | "field" | "method" | "member";

interface SchemaNode {
  label: string;
  kind: SchemaNodeKind;
  file: string;
  children?: SchemaNode[];
}

class IonProjectTreeProvider implements vscode.TreeDataProvider<SchemaNode> {
  private _onDidChangeTreeData = new vscode.EventEmitter<SchemaNode | undefined>();
  readonly onDidChangeTreeData = this._onDidChangeTreeData.event;
  private nodes: SchemaNode[] = [];

  refresh() {
    this.nodes = this.scanWorkspace();
    this._onDidChangeTreeData.fire(undefined);
  }

  getTreeItem(element: SchemaNode): vscode.TreeItem {
    const item = new vscode.TreeItem(
      element.label,
      element.children && element.children.length > 0
        ? vscode.TreeItemCollapsibleState.Collapsed
        : vscode.TreeItemCollapsibleState.None
    );

    item.iconPath = this.getIcon(element.kind);
    item.description = element.kind;
    item.contextValue = element.kind;

    if (element.file) {
      item.command = {
        command: "vscode.open",
        title: "Open",
        arguments: [vscode.Uri.file(element.file)],
      };
    }

    return item;
  }

  getChildren(element?: SchemaNode): SchemaNode[] {
    if (!element) return this.nodes;
    return element.children || [];
  }

  private getIcon(kind: SchemaNodeKind): vscode.ThemeIcon {
    switch (kind) {
      case "msg": return new vscode.ThemeIcon("symbol-struct");
      case "service": return new vscode.ThemeIcon("symbol-interface");
      case "enum": return new vscode.ThemeIcon("symbol-enum");
      case "flags": return new vscode.ThemeIcon("symbol-enum");
      case "union": return new vscode.ThemeIcon("symbol-class");
      case "typedef": return new vscode.ThemeIcon("symbol-type-parameter");
      case "field": return new vscode.ThemeIcon("symbol-field");
      case "method": return new vscode.ThemeIcon("symbol-method");
      case "member": return new vscode.ThemeIcon("symbol-enum-member");
      default: return new vscode.ThemeIcon("symbol-misc");
    }
  }

  private scanWorkspace(): SchemaNode[] {
    const nodes: SchemaNode[] = [];
    const folders = vscode.workspace.workspaceFolders;
    if (!folders) return nodes;

    for (const folder of folders) {
      const ionFiles = this.findIonFiles(folder.uri.fsPath);
      for (const file of ionFiles) {
        const fileNodes = this.parseFileForSymbols(file);
        nodes.push(...fileNodes);
      }
    }

    return nodes;
  }

  private findIonFiles(dir: string): string[] {
    const fs = require("fs");
    const results: string[] = [];
    try {
      const entries = fs.readdirSync(dir, { withFileTypes: true, recursive: true });
      for (const entry of entries) {
        if (entry.isFile() && entry.name.endsWith(".ion")) {
          results.push(path.join(entry.parentPath || entry.path || dir, entry.name));
        }
      }
    } catch {
      // ignore
    }
    return results;
  }

  private parseFileForSymbols(filePath: string): SchemaNode[] {
    const fs = require("fs");
    const nodes: SchemaNode[] = [];
    try {
      const content: string = fs.readFileSync(filePath, "utf8");
      const lines = content.split("\n");

      for (const line of lines) {
        const trimmed = line.trim();
        let match: RegExpMatchArray | null;

        if ((match = trimmed.match(/^msg\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "msg", file: filePath });
        } else if ((match = trimmed.match(/^service\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "service", file: filePath });
        } else if ((match = trimmed.match(/^enum\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "enum", file: filePath });
        } else if ((match = trimmed.match(/^flags\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "flags", file: filePath });
        } else if ((match = trimmed.match(/^union\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "union", file: filePath });
        } else if ((match = trimmed.match(/^typedef\s+(\w+)/))) {
          nodes.push({ label: match[1], kind: "typedef", file: filePath });
        }
      }
    } catch {
      // ignore
    }
    return nodes;
  }
}

// ─── Activation ─────────────────────────────────────────────────────────────

export async function activate(context: vscode.ExtensionContext) {
  outputChannel = vscode.window.createOutputChannel("IonPath Language Server");

  // Status bar
  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  statusBarItem.command = "ionpath.showOutput";
  context.subscriptions.push(statusBarItem);
  updateStatusBar("starting");

  // Tree view
  const treeProvider = new IonProjectTreeProvider();
  const treeView = vscode.window.createTreeView("ionpath.projectView", {
    treeDataProvider: treeProvider,
    showCollapseAll: true,
  });
  context.subscriptions.push(treeView);

  // Refresh tree on file changes
  const watcher = vscode.workspace.createFileSystemWatcher("**/*.ion");
  watcher.onDidChange(() => treeProvider.refresh());
  watcher.onDidCreate(() => treeProvider.refresh());
  watcher.onDidDelete(() => treeProvider.refresh());
  context.subscriptions.push(watcher);
  treeProvider.refresh();

  // Commands
  context.subscriptions.push(
    vscode.commands.registerCommand("ionpath.restartServer", async () => {
      if (client) {
        await client.stop();
        client = undefined;
      }
      if (serverProcess) {
        serverProcess.kill();
        serverProcess = undefined;
      }
      await startServer(context);
    }),
    vscode.commands.registerCommand("ionpath.showOutput", () => {
      outputChannel.show(true);
    })
  );

  // Start server
  await startServer(context);
}

async function startServer(context: vscode.ExtensionContext) {
  updateStatusBar("starting");

  const compilerPath = await ensurePrerequisites(context);
  if (!compilerPath) {
    updateStatusBar("error", "ionc not available");
    return;
  }

  const serverOptions = () => spawnServerAndConnect(compilerPath);

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "ion" }],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher("**/*.ion"),
    },
    outputChannel,
    traceOutputChannel: vscode.window.createOutputChannel("IonPath LSP Trace", { log: true }),
  };

  client = new LanguageClient(
    "ionpath",
    "IonPath Language Server",
    serverOptions,
    clientOptions
  );

  // In dev mode, show the output channel
  if (process.env.IONPATH_DEV === "1") {
    outputChannel.show(true);
  }

  try {
    await client.start();
    updateStatusBar("running");
    outputChannel.appendLine("LSP client started successfully");
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    outputChannel.appendLine(`LSP client failed: ${message}`);
    updateStatusBar("error", message);
    vscode.window.showWarningMessage(
      `IonPath: Could not start language server. Syntax highlighting will still work.\n${message}`
    );
    client = undefined;
  }
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
  if (serverProcess) {
    serverProcess.kill();
    serverProcess = undefined;
  }
}
