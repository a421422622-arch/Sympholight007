"use strict";

const { app, BrowserWindow, dialog, Menu, powerSaveBlocker, Tray } = require("electron");
const { spawn } = require("node:child_process");
const { existsSync } = require("node:fs");
const path = require("node:path");
const { createInterface } = require("node:readline");

const productName = app.getName();
const icon = path.join(__dirname, process.platform === "win32" ? "symphocue.ico" : "symphocue-icon.png");
if (process.platform === "win32") app.setAppUserModelId("SymphoCue");

let mainWindow;
let engine;
let readyEngine;
let engineAddress;
let workbenchNeedsReload = false;
let workbenchLoading = false;
let tray;
let closeDialogOpen = false;
let allowQuit = false;
let quitting = false;
let restartTimer;
const restartAttempts = [];
const rendererRestarts = [];

function showWorkbench() {
  if (!mainWindow || mainWindow.isDestroyed()) return;
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.show();
  mainWindow.focus();
  void reloadWorkbench();
}

function setEngineStatus(status) {
  tray?.setToolTip(`${productName} · ${status}`);
}

async function canExitWhileUnlocked() {
  try {
    const response = await mainWindow.webContents.session.fetch(new URL("/api/access", engineAddress).href, {
      credentials: "include",
      signal: AbortSignal.timeout(2500)
    });
    if (!response.ok) return false;
    return (await response.json()).role != null;
  } catch {
    return !engine && !restartTimer;
  }
}

async function requestWindowClose(fromTray = false) {
  if (closeDialogOpen || allowQuit || !mainWindow || mainWindow.isDestroyed()) return;
  closeDialogOpen = true;
  try {
    if (fromTray) showWorkbench();
    const unlocked = await canExitWhileUnlocked();
    const minimizeLabel = tray && !tray.isDestroyed() ? "最小化到托盘" : "最小化到任务栏";
    const buttons = fromTray && unlocked ? ["取消", "完全退出"] :
      unlocked ? [minimizeLabel, "完全退出", "取消"] : [minimizeLabel, "取消"];
    const { response } = await dialog.showMessageBox(mainWindow, {
      type: "question",
      title: productName,
      message: "关闭 SymphoCue",
      detail: unlocked ? "最小化后会继续运行定时任务和 UDP 输出。完全退出会停止灯控引擎。" :
        "最小化后会继续运行定时任务和 UDP 输出。如需完全退出，请先解锁。",
      buttons,
      defaultId: 0,
      cancelId: fromTray && unlocked ? 0 : buttons.length - 1,
      noLink: true
    });
    if (buttons[response] === minimizeLabel) {
      if (minimizeLabel === "最小化到托盘") mainWindow.hide();
      else mainWindow.minimize();
    }
    if (buttons[response] === "完全退出" && await canExitWhileUnlocked()) {
      allowQuit = true;
      app.quit();
    }
  } catch (error) {
    console.warn("Could not show the close dialog:", error.message);
  } finally { closeDialogOpen = false; }
}

function installTray() {
  tray = new Tray(icon);
  setEngineStatus("引擎运行中");
  tray.on("click", showWorkbench);
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: "打开 SymphoCue", click: showWorkbench },
    { type: "separator" },
    { label: "完全退出…", click: () => void requestWindowClose(true) }
  ]));
}

function projectDirectory() {
  const argument = process.argv.find(value => value.startsWith("--project-directory="));
  if (argument) return path.resolve(argument.slice("--project-directory=".length));
  if (process.env.SHOWCONTROL_DATA_DIR) return path.resolve(process.env.SHOWCONTROL_DATA_DIR);
  const portableData = path.join(path.dirname(process.execPath), "data");
  const legacyData = path.resolve(path.dirname(process.execPath), "..", "ShowControl", "data");
  if (existsSync(path.join(portableData, "project.json"))) return portableData;
  if (process.platform === "win32" && existsSync(path.join(legacyData, "project.json"))) return legacyData;
  const defaultData = path.join(app.getPath("userData"), "data");
  if (!app.commandLine.hasSwitch("user-data-dir") && !existsSync(path.join(defaultData, "project.json"))) {
    const formerDefault = path.join(app.getPath("appData"), "ShowControlDesktop", "data");
    if (existsSync(path.join(formerDefault, "project.json"))) return formerDefault;
  }
  return defaultData;
}

function startEngine(dataDirectory, port = 0) {
  const directory = path.join(process.resourcesPath, "engine");
  const binary = path.join(directory, process.platform === "win32" ? "ShowControl.exe" : "ShowControl");
  const bundled = existsSync(binary);
  const child = spawn(bundled ? binary : "dotnet", bundled ? [] : [path.join(directory, "ShowControl.dll")], {
    cwd: directory,
    windowsHide: true,
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      ...process.env,
      SHOWCONTROL_DESKTOP: "1",
      SHOWCONTROL_DESKTOP_PORT: String(port),
      SHOWCONTROL_DATA_DIR: dataDirectory
    }
  });
  engine = child;
  child.stdin.on("error", () => { /* The engine may exit before its input closes. */ });

  return new Promise((resolve, reject) => {
    let ready = false;
    let diagnostics = "";
    const remember = line => { diagnostics = (diagnostics + line + "\n").slice(-3500); };
    let listeningPort = port;
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error("灯控引擎启动超时。\n" + diagnostics));
    }, 15000);
    child.stderr.setEncoding("utf8").on("data", remember);
    createInterface({ input: child.stdout }).on("line", line => {
      const match = /^SHOWCONTROL_READY (http:\/\/127\.0\.0\.1:\d+)$/.exec(line);
      if (!match || engine !== child) return remember(line);
      ready = true;
      readyEngine = child;
      engineAddress = match[1];
      listeningPort = Number(new URL(match[1]).port);
      clearTimeout(timer);
      setEngineStatus("引擎运行中");
      resolve(match[1]);
    });
    child.once("error", error => {
      clearTimeout(timer);
      if (engine === child) engine = null;
      if (readyEngine === child) readyEngine = null;
      reject(error);
    });
    child.once("exit", code => {
      clearTimeout(timer);
      if (engine === child) engine = null;
      if (readyEngine === child) readyEngine = null;
      if (!ready) return reject(new Error(`灯控引擎启动失败（${code ?? "已退出"}）。\n${diagnostics}`));
      queueRestart(dataDirectory, listeningPort, diagnostics);
    });
  });
}

function queueRestart(dataDirectory, port, diagnostics) {
  if (allowQuit || quitting || !mainWindow || mainWindow.isDestroyed() || restartTimer) return;
  const now = performance.now();
  while (restartAttempts.length && now - restartAttempts[0] >= 5 * 60_000) restartAttempts.shift();
  if (restartAttempts.length >= 3) {
    setEngineStatus("引擎已停止");
    dialog.showErrorBox("灯控引擎恢复失败", `已达到自动恢复次数上限。请检查工程和系统状态，再重新打开 ${productName}。\n` + diagnostics);
    return;
  }
  const delay = 1000 * 2 ** restartAttempts.length;
  setEngineStatus("正在恢复引擎");
  restartAttempts.push(now);
  restartTimer = setTimeout(() => {
    restartTimer = null;
    if (!allowQuit && !quitting && mainWindow && !mainWindow.isDestroyed())
      startEngine(dataDirectory, port)
        .then(reloadWorkbench, error => queueRestart(dataDirectory, port, error.message));
  }, delay);
}

async function reloadWorkbench() {
  if (!workbenchNeedsReload || workbenchLoading || !readyEngine ||
      allowQuit || !mainWindow || mainWindow.isDestroyed()) return;
  const currentEngine = readyEngine;
  workbenchNeedsReload = false;
  workbenchLoading = true;
  let failed = false;
  try { await mainWindow.loadURL(engineAddress); }
  catch (error) {
    failed = workbenchNeedsReload = true;
    console.warn("Workbench recovery failed:", error.message);
  } finally {
    workbenchLoading = false;
    if (!failed || readyEngine !== currentEngine) void reloadWorkbench();
  }
}

async function openWorkbench() {
  const address = await startEngine(projectDirectory());
  const sleepBlocker = powerSaveBlocker.start("prevent-app-suspension");
  app.once("quit", () => powerSaveBlocker.stop(sleepBlocker));
  Menu.setApplicationMenu(null);
  mainWindow = new BrowserWindow({
    title: productName,
    width: 1440,
    height: 950,
    minWidth: 900,
    minHeight: 650,
    show: false,
    backgroundColor: "#10151d",
    icon,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true }
  });
  try { installTray(); }
  catch (error) { console.warn("System tray unavailable:", error.message); }
  mainWindow.on("close", event => {
    if (allowQuit) return;
    event.preventDefault();
    void requestWindowClose();
  });
  mainWindow.on("session-end", () => {
    allowQuit = true;
    engine?.stdin.end();
  });
  mainWindow.webContents.on("render-process-gone", () => {
    if (allowQuit || mainWindow.isDestroyed()) return;
    const now = performance.now();
    while (rendererRestarts.length && now - rendererRestarts[0] >= 5 * 60_000) rendererRestarts.shift();
    if (rendererRestarts.length >= 3) return;
    rendererRestarts.push(now);
    workbenchNeedsReload = true;
    void reloadWorkbench();
  });
  mainWindow.webContents.session.setPermissionRequestHandler((_contents, _permission, respond) => respond(false));
  mainWindow.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  mainWindow.webContents.on("will-navigate", (event, url) => {
    if (new URL(url).origin !== address) event.preventDefault();
  });
  mainWindow.webContents.on("will-prevent-unload", event => {
    const choice = dialog.showMessageBoxSync(mainWindow, {
      type: "warning",
      buttons: ["继续编辑", "退出"],
      defaultId: 0,
      cancelId: 0,
      message: "工程还有未保存的更改。退出后这些更改会丢失。"
    });
    if (choice === 1) event.preventDefault();
    else allowQuit = false;
  });
  mainWindow.once("ready-to-show", () => mainWindow.show());
  await mainWindow.loadURL(address);
}

const enableStartup = process.argv.includes("--enable-autostart");
const disableStartup = process.argv.includes("--disable-autostart");
if (enableStartup || disableStartup) {
  app.whenReady().then(() => {
    if (process.platform !== "win32") throw new Error("此启动项设置适用于 Windows 桌面版。");
    const args = [ `"--project-directory=${projectDirectory()}"`];
    // 复用旧版注册键，让重新注册自启时替换原程序路径。
    app.setLoginItemSettings({ openAtLogin: enableStartup && !disableStartup, path: process.execPath,
      name: "ShowControlDesktop", args });
  }).catch(error => dialog.showErrorBox("无法修改启动项", error.message)).finally(() => app.quit());
} else if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", showWorkbench);
  app.on("activate", showWorkbench);
  app.on("window-all-closed", () => app.quit());
  app.on("will-quit", event => {
    clearTimeout(restartTimer);
    if (!engine?.pid || quitting) return;
    event.preventDefault();
    quitting = true;
    const child = engine;
    const timeout = setTimeout(() => child.kill(), 12000);
    child.once("exit", () => {
      clearTimeout(timeout);
      app.quit();
    });
    child.stdin.end();
  });
  app.whenReady().then(openWorkbench).catch(error => {
    dialog.showErrorBox(`无法打开 ${productName}`, error.message);
    allowQuit = true;
    app.quit();
  });
}
