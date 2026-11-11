"use strict";

const { app, BrowserWindow, dialog, Menu, powerSaveBlocker } = require("electron");
const { spawn } = require("node:child_process");
const { existsSync } = require("node:fs");
const path = require("node:path");
const { createInterface } = require("node:readline");

let mainWindow;
let engine;
let quitting = false;
let restartTimer;
const restartAttempts = [];

function projectDirectory() {
  const argument = process.argv.find(value => value.startsWith("--project-directory="));
  if (argument) return path.resolve(argument.slice("--project-directory=".length));
  if (process.env.SHOWCONTROL_DATA_DIR) return path.resolve(process.env.SHOWCONTROL_DATA_DIR);
  const portableData = path.join(path.dirname(process.execPath), "data");
  const legacyData = path.resolve(path.dirname(process.execPath), "..", "ShowControl", "data");
  if (existsSync(path.join(portableData, "project.json"))) return portableData;
  if (process.platform === "win32" && existsSync(path.join(legacyData, "project.json"))) return legacyData;
  return path.join(app.getPath("userData"), "data");
}

function startEngine(port = 0) {
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
      SHOWCONTROL_LIVE: process.argv.includes("--live") ? "1" : process.argv.includes("--startup") ? "0" : process.env.SHOWCONTROL_LIVE ?? "0",
      SHOWCONTROL_DATA_DIR: projectDirectory()
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
      if (!match) return remember(line);
      ready = true;
      listeningPort = Number(new URL(match[1]).port);
      clearTimeout(timer);
      resolve(match[1]);
    });
    child.once("error", error => {
      clearTimeout(timer);
      if (engine === child) engine = null;
      reject(error);
    });
    child.once("exit", code => {
      clearTimeout(timer);
      if (engine === child) engine = null;
      if (!ready) return reject(new Error(`灯控引擎启动失败（${code ?? "已退出"}）。\n${diagnostics}`));
      queueRestart(listeningPort, diagnostics);
    });
  });
}

function queueRestart(port, diagnostics) {
  if (quitting || !mainWindow || mainWindow.isDestroyed() || restartTimer) return;
  const now = performance.now();
  while (restartAttempts.length && now - restartAttempts[0] >= 5 * 60_000) restartAttempts.shift();
  if (restartAttempts.length >= 3) {
    dialog.showErrorBox("灯控引擎恢复失败", "已达到自动恢复次数上限。请检查工程和系统状态，再重新打开灯控编排台。\n" + diagnostics);
    return;
  }
  const delay = 1000 * 2 ** restartAttempts.length;
  restartAttempts.push(now);
  restartTimer = setTimeout(() => {
    restartTimer = null;
    if (!quitting && mainWindow && !mainWindow.isDestroyed())
      startEngine(port).catch(error => queueRestart(port, error.message));
  }, delay);
}

async function openWorkbench() {
  const address = await startEngine();
  const sleepBlocker = powerSaveBlocker.start("prevent-app-suspension");
  app.once("quit", () => powerSaveBlocker.stop(sleepBlocker));
  Menu.setApplicationMenu(null);
  mainWindow = new BrowserWindow({
    title: "灯控编排台",
    width: 1440,
    height: 950,
    minWidth: 900,
    minHeight: 650,
    show: false,
    backgroundColor: "#101615",
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true }
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
  });
  mainWindow.once("ready-to-show", () => mainWindow.show());
  await mainWindow.loadURL(address);
}

const enableStartup = process.argv.includes("--enable-autostart");
const disableStartup = process.argv.includes("--disable-autostart");
if (enableStartup || disableStartup) {
  app.whenReady().then(() => {
    if (process.platform !== "win32") throw new Error("此启动项设置适用于 Windows 桌面版。");
    const args = ["--startup", `"--project-directory=${projectDirectory()}"`];
    if (process.argv.includes("--live")) args.push("--live");
    app.setLoginItemSettings({ openAtLogin: enableStartup && !disableStartup, path: process.execPath,
      name: "ShowControlDesktop", args });
  }).catch(error => dialog.showErrorBox("无法修改启动项", error.message)).finally(() => app.quit());
} else if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (!mainWindow || mainWindow.isDestroyed()) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });
  app.on("window-all-closed", () => app.quit());
  app.on("will-quit", event => {
    clearTimeout(restartTimer);
    if (!engine?.pid || quitting) return;
    event.preventDefault();
    quitting = true;
    const child = engine;
    const timeout = setTimeout(() => child.kill(), 4000);
    child.once("exit", () => {
      clearTimeout(timeout);
      app.quit();
    });
    child.stdin.end();
  });
  app.whenReady().then(openWorkbench).catch(error => {
    dialog.showErrorBox("无法打开灯控编排台", error.message);
    app.quit();
  });
}
