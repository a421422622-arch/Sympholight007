#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

const root = path.resolve(__dirname, "../..");
const usage = `SymphoCue 多智能体测试

node tests/MultiAgent/run.cjs --exe <SymphoCue.exe> [--playwright <模块路径>] [--dotnet <dotnet>] [--fast]

--exe         要验证的桌面程序完整路径
--playwright  已安装的 playwright 模块目录。不指定时从当前项目查找
--dotnet      .NET 10 SDK 的 dotnet 路径，默认从 PATH 查找
--fast        跳过真实分钟定时触发验证
`;

function argumentsFrom(argv) {
  const options = { dotnet: "dotnet", fast: false };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") { console.log(usage); process.exit(0); }
    if (arg === "--fast") { options.fast = true; continue; }
    if (["--exe", "--playwright", "--dotnet"].includes(arg) && argv[i + 1]) {
      options[arg.slice(2)] = argv[++i];
      continue;
    }
    throw new Error("未知参数或缺少参数值 " + arg);
  }
  if (!options.exe) throw new Error("请使用 --exe 指定桌面程序路径。");
  options.exe = path.resolve(options.exe);
  if (!fs.existsSync(options.exe)) throw new Error("找不到桌面程序 " + options.exe);
  if (options.playwright) options.playwright = require.resolve(path.resolve(options.playwright));
  else options.playwright = require.resolve("playwright", { paths: [__dirname, root] });
  return options;
}

function run(name, executable, args, { env, input } = {}) {
  return new Promise(resolve => {
    const started = new Date();
    console.log(`\n▶ ${name}`);
    let stdout = "", stderr = "";
    const child = spawn(executable, args, { cwd: root, env: { ...process.env, ...env }, windowsHide: true });
    child.stdout.setEncoding("utf8").on("data", text => { stdout += text; process.stdout.write(text); });
    child.stderr.setEncoding("utf8").on("data", text => { stderr += text; process.stderr.write(text); });
    let settled = false;
    function finish(code, error) {
      if (settled) return;
      settled = true;
      const passed = code === 0 && !error;
      console.log(`${passed ? "✓" : "✗"} ${name}`);
      resolve({ name, status: passed ? "passed" : "failed", code, started: started.toISOString(),
        finished: new Date().toISOString(), stdout, stderr, error: error?.message });
    }
    child.on("error", error => finish(null, error));
    child.on("close", code => finish(code));
    child.stdin.on("error", () => {});
    child.stdin.end(input);
  });
}

async function main() {
  const options = argumentsFrom(process.argv.slice(2));
  const reportDir = fs.mkdtempSync(path.join(os.tmpdir(), "symphocue-test-report-"));
  const report = { version: 1, started: new Date().toISOString(), executable: options.exe,
    mode: options.fast ? "快速" : "完整", tests: [],
    limitations: ["仅使用隔离工程及本机回环。", "网卡掉线与恢复的界面状态由测试数据模拟。",
      "物理网卡拔插、现场设备和多日连续运行未测试。"] };
  if (options.fast) report.limitations.push("快速模式跳过锁屏后真实分钟定时触发。");
  const env = { SYMPHOCUE_TEST_EXE: options.exe, SYMPHOCUE_PLAYWRIGHT: options.playwright,
    REVIEW_FAST: options.fast ? "1" : "" };
  report.tests.push(await run("引擎与 UDP 回环", options.dotnet,
    ["run", "--project", path.join(root, "tests/ShowControl.Regression"), "-c", "Release",
      "--artifacts-path", path.join(reportDir, "dotnet"), "--property:NuGetAudit=false"]));
  report.tests.push(await run("网卡选择与状态", process.execPath, [path.join(__dirname, "network-ui.cjs")], { env }));
  report.tests.push(await run("锁屏、托盘和退出", process.execPath, [path.join(__dirname, "desktop-lifecycle.cjs")], { env }));

  report.finished = new Date().toISOString();
  report.status = report.tests.every(test => test.status === "passed") ? "passed" : "failed";
  const file = path.join(reportDir, "report.json");
  fs.writeFileSync(file, JSON.stringify(report, null, 2), "utf8");
  console.log(`\n测试结果 ${report.status === "passed" ? "通过" : "存在失败或未完成"}\n报告 ${file}`);
  for (const limitation of report.limitations) console.log("· " + limitation);
  if (report.status !== "passed") process.exitCode = 1;
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
