"use strict";

const { app, BrowserWindow, dialog, Menu, powerSaveBlocker } = require("electron");
const { spawn } = require("node:child_process");
const { existsSync } = require("node:fs");
const path = require("node:path");
const { createInterface } = require("node:readline");

let mainWindow;
let engine;
let quitting = false;

function projectDirectory() {
  if (process.env.SHOWCONTROL_DATA_DIR) return path.resolve(process.env.SHOWCONTROL_DATA_DIR);
  const portableData = path.join(path.dirname(process.execPath), "data");
  const legacyData = path.resolve(path.dirname(process.execPath), "..", "ShowControl", "data");
  if (existsSync(path.join(portableData, "project.json"))) return portableData;
  if (process.platform === "win32" && existsSync(path.join(legacyData, "project.json"))) return legacyData;
  return path.join(app.getPath("userData"), "data");
}

function startEngine() {
  const directory = path.join(process.resourcesPath, "engine");
  const binary = path.join(directory, process.platform === "win32" ? "ShowControl.exe" : "ShowControl");
  const bundled = existsSync(binary);
  engine = spawn(bundled ? binary : "dotnet", bundled ? [] : [path.join(directory, "ShowControl.dll")], {
    cwd: directory,
    windowsHide: true,
    stdio: ["pipe", "pipe", "pipe"],
    env: {
      ...process.env,
      SHOWCONTROL_DESKTOP: "1",
      SHOWCONTROL_DATA_DIR: projectDirectory()
    }
  });
  engine.stdin.on("error", () => { /* The engine may exit before its input closes. */ });

  return new Promise((resolve, reject) => {
    let ready = false;
    let diagnostics = "";
    const remember = line => { diagnostics = (diagnostics + line + "\n").slice(-3500); };
    const timer = setTimeout(() => reject(new Error("灯控引擎启动超时。\n" + diagnostics)), 15000);
    engine.stderr.setEncoding("utf8").on("data", remember);
    createInterface({ input: engine.stdout }).on("line", line => {
      const match = /^SHOWCONTROL_READY (http:\/\/127\.0\.0\.1:\d+)$/.exec(line);
      if (!match) return remember(line);
      ready = true;
      clearTimeout(timer);
      resolve(match[1]);
    });
    engine.once("error", error => {
      clearTimeout(timer);
      reject(error);
    });
    engine.once("exit", code => {
      clearTimeout(timer);
      engine = null;
      if (!ready) return reject(new Error(`灯控引擎启动失败（${code ?? "已退出"}）。\n${diagnostics}`));
      if (!quitting) {
        dialog.showErrorBox("灯控引擎已退出", diagnostics || "请重新打开灯控编排台。");
        app.quit();
      }
    });
  });
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

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (!mainWindow || mainWindow.isDestroyed()) return;
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
  });
  app.on("window-all-closed", () => app.quit());
  app.on("will-quit", event => {
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
