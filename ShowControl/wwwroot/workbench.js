"use strict";

const $ = id => document.getElementById(id);
const esc = value => String(value ?? "").replace(/[&<>"']/g, ch => ({
  "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
}[ch]));
const ids = () => crypto.randomUUID();
const policies = { retry: "重试", skip: "跳过", stop: "停止列表" };
const healthLabels = { online: "在线", offline: "离线", unknown: "待检测" };
const softwareLabels = { online: "在线", unconfirmed: "未确认", unknown: "待检测", notConfigured: "未配置" };
const replyModes = { none: "不等待回执", exact: "匹配自定义回执" };
const defaultLayout = { order: ["project", "workspace", "properties", "log"], sizes: {} };
const adminActions = new Set(["rename-project", "edit-show", "add-group", "edit-group", "add-list", "edit-list",
  "add-cue", "add-target", "remove-target", "delete-cue", "up", "down", "edit-resource", "delete-resource",
  "delete-current", "import-ips", "export-ips", "edit-ip-health", "edit-health-interval"]);
const scheduleModes = { weekly: "平日 · 周循环", date: "节目 · 指定日", holiday: "节目 · 节假日" };
const weekdays = [[1, "一"], [2, "二"], [3, "三"], [4, "四"], [5, "五"], [6, "六"], [0, "日"]];
const sourceLabels = { manual: "手动播放", pad: "Action Pad", schedule: "定时任务" };
const playbackModes = { once: "顺序播放一次", loop: "循环播放" };
const playbackSecondsInput = { type: "number", min: 0, max: 3600, step: 0.001 };
const eventLabels = {
  queued: "已接收", send: "发送 UDP", ping: "Ping 检查", wait: "等待回执", ack: "已确认",
  success: "成功", failed: "失败", retry: "重试", skipped: "跳过", stopped: "停止",
  priority: "优先调度", interval: "播放间隔", adapterWait: "等待网卡", adapterReady: "网卡恢复",
  completed: "完成", completedWithSkips: "完成 · 有跳过"
};
const state = {
  project: null, revision: 0, dirty: false, tab: "cues", listId: null, cueId: null,
  health: [], filters: { search: "", group: "", status: "" }, runs: [], timezone: "", scheduleMode: "weekly",
  editor: null, saving: false, editCounter: 0, stoppingRuns: new Set(), stoppingAll: false,
  countdowns: new Map(), importingIps: false, access: { configured: false, role: null },
  layout: defaultLayout, layoutSaving: false, treePage: 1, treeOpen: new Map(),
  healthPage: 1, healthPageSize: 25, healthScan: null, healthVersion: -1, healthFetching: false
};
let noticeTimer;
let statusRequest = 0;

function notice(message, error = false) {
  clearTimeout(noticeTimer);
  $("notice").textContent = message;
  $("notice").classList.toggle("error", error);
  $("notice").hidden = false;
  noticeTimer = setTimeout(() => { $("notice").hidden = true; }, error ? 11000 : 3600);
}

async function request(url, method = "GET", data) {
  const options = { method };
  if (data instanceof FormData) {
    options.body = data;
  } else if (data !== undefined) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(data);
  }
  const response = await fetch("/api" + url, options);
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.message || body.detail || `请求失败 (${response.status})`);
  return body;
}

function lists() {
  return state.project.shows.flatMap(show => show.groups.flatMap(group =>
    group.lists.map(list => ({ show, group, list }))));
}

function selected() {
  return lists().find(item => item.list.id === state.listId);
}

function selectedCue() {
  return selected()?.list.cues.find(cue => cue.id === state.cueId);
}

function targets(cue) {
  return [cue, ...(cue.additionalTargets ?? [])];
}

function responseFor(cue, target) {
  const policy = target.response ?? { mode: "exact", text: cue.reply, timeoutMs: cue.timeoutMs };
  return { ...policy, timeoutMs: policy.timeoutMs ?? 1800 };
}

function ensureSelection() {
  if (!selected()) state.listId = lists()[0]?.list.id ?? null;
  if (!selectedCue()) state.cueId = selected()?.list.cues[0]?.id ?? null;
}

function dirty() {
  state.editCounter++;
  state.dirty = true;
  updateSave();
}

function updateSave() {
  $("save").disabled = !state.dirty || state.saving;
  $("save").textContent = state.saving ? "保存中…" : state.dirty ? "保存项目 *" : "保存项目";
}

function option(value, label, selected = "") {
  return `<option value="${esc(value)}"${value === selected ? " selected" : ""}>${esc(label)}</option>`;
}

function button(label, action, id = "", kind = "", disabled = false) {
  return `<button type="button" class="button ${kind}${adminActions.has(action) ? " admin-only" : ""}" data-action="${action}" data-id="${esc(id)}"${disabled ? " disabled" : ""}>${esc(label)}</button>`;
}

function badge(label, kind = "") {
  return `<span class="badge ${kind}">${esc(label)}</span>`;
}

function table(headers, rows, empty = "暂无记录") {
  return `<div class="scroll table-box"><table><thead><tr>${headers.map(item => `<th scope="col">${esc(item)}</th>`).join("")}</tr></thead><tbody>${rows || `<tr><td colspan="${headers.length}">${esc(empty)}</td></tr>`}</tbody></table></div>`;
}

function heading(title, hint, actions = "", breadcrumb = "") {
  return `<div class="section-top"><div><div class="breadcrumb">${esc(breadcrumb || "02 / 工作区")}</div><h1>${esc(title)}</h1><p>${esc(hint)}</p></div><div class="actions">${actions}</div></div>`;
}

function pageView(items, page, size) {
  const pages = Math.max(1, Math.ceil(items.length / size));
  const current = Math.max(1, Math.min(page, pages));
  return { items: items.slice((current - 1) * size, current * size), current, pages, total: items.length };
}

function pageControls(page, unit) {
  return `<button class="icon" type="button" data-page-step="-1"${page.current <= 1 ? " disabled" : ""}>上一页</button>
    <span>${page.current} / ${page.pages} · ${page.total} ${unit}</span>
    <button class="icon" type="button" data-page-step="1"${page.current >= page.pages ? " disabled" : ""}>下一页</button>`;
}

function matchingShows(query) {
  if (!query) return state.project.shows;
  const matches = name => name.toLowerCase().includes(query);
  return state.project.shows.flatMap(show => {
    if (matches(show.name)) return [show];
    const groups = show.groups.flatMap(group => {
      if (matches(group.name)) return [group];
      const lists = group.lists.filter(list => matches(list.name) || list.cues.some(cue => matches(cue.name)));
      return lists.length ? [{ ...group, lists }] : [];
    });
    return groups.length ? [{ ...show, groups }] : [];
  });
}

function renderTree() {
  const query = $("projectSearch").value.trim().toLowerCase();
  if (!$("tree").dataset.searching)
    $("tree").querySelectorAll("details").forEach(item => state.treeOpen.set(item.dataset.id, item.open));
  $("tree").dataset.searching = query ? "true" : "";
  const page = pageView(matchingShows(query), state.treePage, 8);
  state.treePage = page.current;
  $("treePager").innerHTML = pageControls(page, "个演出");
  const current = selected();
  const open = (id, selectedId) => query || id === selectedId || state.treeOpen.get(id) ? " open" : "";
  const manage = (action, id, label) => `<button type="button" class="manage admin-only" title="${label}" aria-label="${label}" data-action="${action}" data-id="${esc(id)}">···</button>`;
  $("tree").innerHTML = page.items.map(show => `
    <details data-id="${esc(show.id)}"${open(show.id, current?.show.id)}>
      <summary><span>${esc(show.name)}</span>${manage("edit-show", show.id, "管理演出")}</summary>
      ${show.groups.map(group => `<details class="group" data-id="${esc(group.id)}"${open(group.id, current?.group.id)}>
        <summary><span>${esc(group.name)}</span>${manage("edit-group", group.id, "管理分组")}</summary>
        ${group.lists.map(list => `<div class="list-row"><button type="button" data-action="select-list" data-id="${esc(list.id)}" class="list${list.id === state.listId ? " selected" : ""}">▤ ${esc(list.name)}</button>
          <button class="icon tree-transport" type="button" data-action="tree-play" data-id="${esc(list.id)}" data-empty="${!list.cues.length}" aria-label="播放 ${esc(list.name)}" title="播放" ${!list.cues.length ? "disabled" : ""}>▶</button>
          <button class="icon tree-transport danger" type="button" data-action="stop-selected" data-id="${esc(list.id)}" aria-label="停止 ${esc(list.name)}" title="停止" disabled>■</button></div>
          ${list.cues.filter(cue => list.id === state.listId || (query && cue.name.toLowerCase().includes(query)))
            .map(cue => `<button type="button" class="cue-link${cue.id === state.cueId ? " selected" : ""}" data-action="select-cue"
              data-id="${esc(cue.id)}" data-list-id="${esc(list.id)}">· ${esc(cue.name)}</button>`).join("")}`).join("")}
        <button type="button" class="add-child admin-only" data-action="add-list" data-id="${esc(group.id)}">＋ Cue 列表</button>
      </details>`).join("")}
      <button type="button" class="add-child admin-only" data-action="add-group" data-id="${esc(show.id)}">＋ 分组</button>
    </details>`).join("") || `<div class="empty">${query ? "没有匹配的项目条目。" : "用 ＋ 添加第一个演出。"}</div>`;
  $("ipCount").textContent = state.project.ipGroups.length;
  $("templateCount").textContent = state.project.templates.length;
}

function render() {
  ensureSelection();
  $("projectName").textContent = state.project.name;
  renderTree();
  document.querySelectorAll("[data-tab]").forEach(el => {
    el.classList.toggle("selected", el.dataset.tab === state.tab);
    el.setAttribute("aria-pressed", String(el.dataset.tab === state.tab));
  });
  $("propertiesPanel").hidden = state.tab !== "cues";
  if (state.tab === "cues") renderCues();
  else if (state.tab === "health") renderHealth();
  else renderResource();
  applyLayout();
  updateTransport();
  updateSave();
}

function applyLayout(layout = state.layout) {
  const grid = $("panelGrid");
  const panels = new Map([...grid.children].map(panel => [panel.dataset.panel, panel]));
  if ([...panels.keys()].join() !== layout.order.join())
    layout.order.forEach(id => grid.append(panels.get(id)));
  const visible = layout.order.filter(id => !panels.get(id).hidden);
  const widths = { project: "clamp(175px, 17vw, 224px)", properties: "clamp(215px, 22vw, 288px)",
    workspace: "minmax(0,1fr)", log: "minmax(0,1fr)" };
  for (const [id, panel] of panels) {
    const size = layout.sizes?.[id];
    panel.classList.toggle("dock-wide", id === visible.at(-1));
    panel.classList.toggle("sized-width", size?.width != null);
    panel.classList.toggle("sized-height", size?.height != null);
    panel.style.setProperty("--panel-width", size?.width == null ? "" : `${size.width}px`);
    panel.style.setProperty("--panel-height", size?.height == null ? "" : `${size.height}px`);
    panel.style.setProperty("--panel-inset-x", size?.insetX == null ? "" : `${size.insetX}px`);
    panel.style.setProperty("--panel-inset-y", size?.insetY == null ? "" : `${size.insetY}px`);
  }
  grid.style.setProperty("--dock-columns", visible.slice(0, -1).map(id => {
    const size = layout.sizes?.[id];
    return size?.width == null ? widths[id] : `minmax(0,${size.width + (size.insetX ?? 0)}px)`;
  }).join(" "));
}

async function saveLayout(layout) {
  if (state.layoutSaving || state.access.role !== "admin") { applyLayout(); return; }
  state.layoutSaving = true;
  try {
    state.layout = await request("/layout", "PUT", layout);
  } catch (error) { notice(error.message, true); }
  finally { state.layoutSaving = false; applyLayout(); }
}

function swapPanels(first, second) {
  if (!first || !second || first === second) return;
  const order = [...state.layout.order];
  const a = order.indexOf(first), b = order.indexOf(second);
  if (a < 0 || b < 0) return;
  [order[a], order[b]] = [order[b], order[a]];
  return saveLayout({ ...state.layout, order });
}

function resizedLayout(id, bounds, dx, dy) {
  const size = { ...state.layout.sizes?.[id] };
  if (dx) size.width = Math.round(Math.max(140, Math.min(2400, bounds.width + dx)));
  if (dy) size.height = Math.round(Math.max(110, Math.min(2400, bounds.height + dy)));
  return { ...state.layout, sizes: { ...state.layout.sizes, [id]: size } };
}

function resizeContext(panel, edge) {
  const id = panel.dataset.panel;
  const bounds = panel.getBoundingClientRect();
  const peers = [...$("panelGrid").children].filter(other => other !== panel && !other.hidden)
    .map(other => ({ id: other.dataset.panel, bounds: other.getBoundingClientRect() }));
  const near = (a, b) => Math.abs(a - b) <= 2;
  const verticalOverlap = rect => rect.top < bounds.bottom && rect.bottom > bounds.top;
  const horizontalOverlap = rect => rect.left < bounds.right && rect.right > bounds.left;
  let neighbor = null;
  if (edge === "left" || edge === "right") {
    neighbor = peers.find(({ bounds: rect }) => verticalOverlap(rect) &&
      (edge === "left" ? near(rect.right, bounds.left) : near(rect.left, bounds.right)));
  }
  let above = [];
  if (edge === "top") {
    const row = peers.filter(({ bounds: rect }) => horizontalOverlap(rect) && rect.bottom <= bounds.top + 2 &&
      rect.top < bounds.top - 2);
    const touching = row.find(({ bounds: rect }) => near(rect.bottom, bounds.top));
    if (touching) above = row.filter(({ bounds: rect }) => rect.bottom > touching.bounds.top);
  }
  return { id, bounds, edge, neighbor, above };
}

function resizeFromEdge(context, dx, dy) {
  const { id, bounds, edge, neighbor, above } = context;
  if (!edge) return resizedLayout(id, bounds, dx, dy);
  const sizes = { ...state.layout.sizes };
  const set = (panel, size) => { sizes[panel] = { ...sizes[panel], ...size }; };
  const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

  if (edge === "left" || edge === "right") {
    if (neighbor) {
      const left = edge === "left" ? neighbor : { id, bounds };
      const right = edge === "left" ? { id, bounds } : neighbor;
      const change = clamp(dx, Math.max(140 - left.bounds.width, right.bounds.width - 2400),
        Math.min(2400 - left.bounds.width, right.bounds.width - 140));
      set(left.id, { width: Math.round(left.bounds.width + change) });
      set(right.id, { width: Math.round(right.bounds.width - change) });
    } else if (edge === "left") {
      const inset = sizes[id]?.insetX ?? 0;
      const change = clamp(dx, Math.max(-inset, bounds.width - 2400), Math.min(2400 - inset, bounds.width - 140));
      set(id, { width: Math.round(bounds.width - change), insetX: Math.round(inset + change) });
    } else {
      set(id, { width: Math.round(clamp(bounds.width + dx, 140, 2400)) });
    }
  } else if (edge === "top" && above.length) {
    const touching = above.filter(({ bounds: rect }) => Math.abs(rect.bottom - bounds.top) <= 2);
    const change = clamp(dy, Math.max(bounds.height - 2400, ...above.map(({ bounds: rect }) => rect.top + 110 - bounds.top)),
      Math.min(bounds.height - 110, ...touching.map(({ bounds: rect }) => 2400 - rect.height)));
    const boundary = bounds.top + change;
    for (const item of above)
      if (item.bounds.bottom > boundary || touching.includes(item))
        set(item.id, { height: Math.round(clamp(boundary - item.bounds.top, 110, 2400)) });
    set(id, { height: Math.round(bounds.height - change) });
  } else if (edge === "top") {
    const inset = sizes[id]?.insetY ?? 0;
    const change = clamp(dy, Math.max(-inset, bounds.height - 2400), Math.min(2400 - inset, bounds.height - 110));
    set(id, { height: Math.round(bounds.height - change), insetY: Math.round(inset + change) });
  } else {
    set(id, { height: Math.round(clamp(bounds.height + dy, 110, 2400)) });
  }
  return { ...state.layout, sizes };
}

function applyAccess(access) {
  const wasLocked = state.access.role === null;
  state.access = access;
  const locked = access.role === null;
  document.body.classList.toggle("operator", access.role === "operator");
  $("lock").textContent = "锁屏";
  $("permissions").textContent = access.configured ? "权限" : "设置权限";
  $("appTop").inert = $("panelGrid").inert = locked;
  $("lockScreen").hidden = !locked;
  if (locked && $("editor").open) $("editor").close();
  if (locked && $("logFolderDialog").open) $("logFolderDialog").close();
  if (locked && !wasLocked) $("unlockForm").elements.password.focus();
}

async function lockAccess() {
  if (state.dirty || state.saving) throw new Error("请先保存或重新载入工程，再锁屏。");
  applyAccess(await request("/access/lock", "POST"));
  await poll();
}

function editPermissions(lockAfter = false) {
  const configured = state.access.configured;
  edit("工作站权限",
    field(configured ? "新管理员密码（留空保留）" : "管理员密码（至少 8 位）", "adminPassword", "",
      { type: "password", required: !configured }) +
    field("操作员密码（可选，留空保留）", "operatorPassword", "", { type: "password" }) +
    (configured ? field("移除操作员密码", "removeOperator", false, { type: "checkbox" }) : "") +
    '<p class="property-help">管理员可以配置工程。操作员可播放、停止和查看状态。锁屏后需解锁才可操作。</p>',
    async data => {
      applyAccess(await request("/access/settings", "POST", {
        adminPassword: data.get("adminPassword"), operatorPassword: data.get("operatorPassword"),
        removeOperator: data.has("removeOperator")
      }));
      if (lockAfter) await lockAccess();
      else notice("权限设置已保存。");
    }, null, false);
}

async function editUdpAdapter() {
  if (!state.project || state.access.role !== "admin") return;
  const adapters = await request("/udp-interfaces");
  if (state.access.role !== "admin" || $("editor").open) return;
  const current = state.project.udpInterfaceId ?? "";
  const routes = state.project.udpRoutes ?? [];
  const choices = adapters.map(adapter => [adapter.id,
    `${adapter.name} · ${adapter.up && adapter.addresses.length ? adapter.addresses.join(" / ") : "未就绪"}`]);
  const known = new Set(choices.map(([id]) => id));
  for (const id of new Set([current, ...routes.map(route => route.interfaceId)]))
    if (id && !known.has(id)) choices.push([id, `已保存网卡 · 当前未检测到 · ${id}`]);
  const suggested = [...new Set(adapters.flatMap(adapter => adapter.networks ?? []))];
  const routeRow = (route = {}) => '<div class="udp-route" data-udp-route>' +
    field("目标网段", "network", route.network ?? "", { required: true, placeholder: "192.168.10.0/24", suggestions: "udpNetworks" }) +
    field("发送网卡", "interfaceId", route.interfaceId ?? "", { choices: [["", "请选择网卡"], ...choices] }) +
    '<button type="button" class="button quiet" data-remove-route>移除</button></div>';
  edit("UDP 网卡与网段",
    table(["当前本机网卡", "地址", "状态"], adapters.map(adapter => `<tr><td>${esc(adapter.name)}</td><td>${esc(adapter.addresses.join(" / ") || "—")}</td><td>${adapter.up && adapter.addresses.length ? "可用" : "未就绪"}</td></tr>`).join("")) +
    field("未匹配网段的默认网卡", "adapter", current, { choices: [["", "系统自动路由"], ...choices] }) +
    '<div id="udpRoutes">' + routes.map(route => routeRow(route)).join("") + "</div>" +
    '<button type="button" class="button quiet" id="addUdpRoute">＋ 添加网段与网卡</button>' +
    `<datalist id="udpNetworks">${suggested.map(network => option(network, network)).join("")}</datalist>` +
    '<p class="property-help">127.0.0.1 和 ::1 始终走本机回环。其他目标按最具体的网段匹配网卡。保存工程后用于新触发的列表与软件健康查询。</p>',
    data => {
      const routes = [...$("udpRoutes").querySelectorAll("[data-udp-route]")].map(row => ({
        network: row.querySelector('[name="network"]').value.trim(),
        interfaceId: row.querySelector('[name="interfaceId"]').value
      }));
      if (routes.some(route => !route.network || !route.interfaceId)) throw new Error("请为每条规则填写目标网段和发送网卡。");
      if (routes.length > 128) throw new Error("最多配置 128 条 UDP 网段规则。");
      Object.assign(state.project, { udpInterfaceId: data.get("adapter") || null, udpRoutes: routes });
    });
  $("addUdpRoute").addEventListener("click", () => {
    $("udpRoutes").insertAdjacentHTML("beforeend", routeRow());
    $("udpRoutes").lastElementChild.querySelector('[name="network"]').focus();
  });
  $("udpRoutes").addEventListener("click", event => {
    event.target.closest("[data-remove-route]")?.closest("[data-udp-route]").remove();
  });
}

function renderCues() {
  const current = selected();
  if (!current) {
    $("main").innerHTML = heading("Cue 编排", "从项目树选择或创建 Cue 列表。");
    $("properties").innerHTML = '<span class="eyebrow">属性</span><h2>未选择 Cue</h2>';
    return;
  }
  const { show, group, list } = current;
  const actions = button("列表设置", "edit-list", list.id, "quiet") +
    button("＋ 新增 Cue", "add-cue");
  const mode = list.playbackMode ?? "once";
  const transport = `<div class="playback-bar">
    <div class="transport" role="group" aria-label="播放控制">${button("▶ 播放", "run", list.id, "strong", !list.cues.length)}
      ${button("■ 停止", "stop-selected", list.id, "danger", true)}</div>
    <span class="playback-mode">播放组 ${esc(list.playbackGroup ?? "默认")} · ${esc(playbackModes[mode])} · 默认间隔 ${esc(waitLabel(list.intervalMs ?? 0))}</span>
    <span id="playbackStatus" class="playback-status" role="status"></span>
    <div id="triggerCountdown" class="trigger-countdown" hidden>
      <span>触发倒计时</span><span id="triggerTime" role="timer" aria-label="下一条 Cue 触发倒计时"></span>
      <span id="triggerCue"></span>
    </div></div>`;
  const rows = list.cues.map((cue, index) => {
    const targetNames = targets(cue).map(target => state.project.ipGroups.find(item => item.id === target.ipGroupId)?.name || "未设置");
    const replyModes = new Set(targets(cue).map(target => responseFor(cue, target).mode));
    const verification = replyModes.size > 1 ? "混合回执" : replyModes.has("exact") ? "匹配回执" : "不等回执";
    const delay = cueWaitLabel(list, cue);
    return `<tr data-cue-row="${esc(cue.id)}" class="${cue.id === state.cueId ? "selected" : ""}"><td class="mono">${String(index + 1).padStart(2, "0")}</td>
      <td><button class="row-select" type="button" data-action="select-cue" data-id="${esc(cue.id)}">${esc(cue.name)}</button></td>
      <td>${esc(targetNames.join(" / "))}</td><td class="cue-delay">${esc(delay)}</td>
      <td>${badge((cue.ping ? "ICMP Ping · " : "") + verification, cue.ping ? "good" : "")}</td>
      <td>${esc(policies[cue.onFail])}</td><td><div class="order">
      <button class="icon admin-only" data-action="up" data-id="${esc(cue.id)}" type="button" aria-label="上移"${index === 0 ? " disabled" : ""}>↑</button>
      <button class="icon admin-only" data-action="down" data-id="${esc(cue.id)}" type="button" aria-label="下移"${index === list.cues.length - 1 ? " disabled" : ""}>↓</button></div></td></tr>`;
  }).join("");
  $("main").innerHTML = heading(list.name, `${list.cues.length} 条 Cue · 按顺序执行 · 手动及 Action Pad 优先`,
    actions, `${show.name} / ${group.name} / Cue 编排`) + transport +
    table(["序号", "Cue 名称", "目标分组", "完成后等待", "校验流程", "失败策略", "排序"], rows, "列表为空。添加 Cue 后即可执行。");
  renderProperties();
  updateTransport();
}

function waitLabel(ms) {
  return ms === 0 ? "无等待" : `${ms / 1000} 秒`;
}

function cueWaitLabel(list, cue) {
  if (list.playbackMode !== "loop" && list.cues.at(-1) === cue) return "播放结束";
  return cue.delayAfterMs == null ? `默认 · ${waitLabel(list.intervalMs ?? 0)}` : waitLabel(cue.delayAfterMs);
}

function selectedRun() {
  return state.runs.findLast(run => run.listId === state.listId);
}

function isStopping(run) {
  return run.stopping || state.stoppingAll || state.stoppingRuns.has(run.id);
}

function updateCountdown() {
  const display = $("triggerCountdown");
  if (!display) return;
  const active = selectedRun();
  const countdown = state.countdowns.get(active?.id);
  display.hidden = !countdown || isStopping(active);
  if (display.hidden) return;

  const seconds = Math.max(0, Math.ceil((countdown.deadline - performance.now()) / 1000));
  const remaining = [Math.floor(seconds / 60), seconds % 60].map(value => String(value).padStart(2, "0")).join(":");
  const triggerTime = $("triggerTime");
  if (triggerTime.textContent !== remaining) triggerTime.textContent = remaining;
  const triggerCue = $("triggerCue");
  const next = `下一条 · ${countdown.nextCue}`;
  if (triggerCue.textContent !== next) triggerCue.textContent = next;
}

function updateTransport() {
  const active = selectedRun();
  const stopping = !!active && isStopping(active);
  const stopSelected = $("main").querySelector('[data-action="stop-selected"]');
  if (stopSelected) {
    stopSelected.disabled = !active || stopping;
    stopSelected.textContent = stopping ? "正在停止…" : "■ 停止";
    $("main").querySelector('[data-action="run"]').disabled =
      !selected()?.list.cues.length || state.stoppingAll || (!!active && (active.source !== "schedule" || stopping));
    const playbackStatus = $("playbackStatus");
    let label = "待播放";
    if (active) {
      const round = active.playbackMode === "loop" && active.round ? `第 ${active.round} 轮 · ` : "";
      label = stopping ? "正在停止…" : active.cueName
        ? round + (active.waitingForNext ? "等待下一步 · " : "正在执行 · ") + active.cueName : "准备播放…";
    }
    if (playbackStatus.textContent !== label) playbackStatus.textContent = label;
    $("main").querySelectorAll("[data-cue-row]").forEach(row => {
      row.classList.toggle("playing", active?.cueId === row.dataset.cueRow);
    });
  }
  $("stop").disabled = state.stoppingAll || !state.runs.some(run => !run.stopping);
  $("stop").textContent = state.stoppingAll ? "正在停止…" : "■ 全部停止";
  $("active").classList.toggle("running", state.runs.length > 0);
  $("active").textContent = state.runs.length ? `${state.runs.length} 个列表运行中` : "引擎待命";
  $("active").title = state.runs.map(run => `${run.playbackGroup ?? "默认"} · ${run.list}`).join("\n");
  const runningLists = new Set(state.runs.map(run => run.listId));
  $("tree").querySelectorAll('[data-action="select-list"]').forEach(item => {
    item.classList.toggle("playing", runningLists.has(item.dataset.id));
  });
  $("tree").querySelectorAll('[data-action="tree-play"],[data-action="stop-selected"]').forEach(item => {
    const run = state.runs.findLast(entry => entry.listId === item.dataset.id);
    item.disabled = item.dataset.action === "tree-play"
      ? item.dataset.empty === "true" || state.stoppingAll || (!!run && (run.source !== "schedule" || isStopping(run)))
      : !run || isStopping(run);
  });
  updateCountdown();
}

function field(label, name, value, { type = "text", choices, wide = false, required = false, min, max, step, placeholder, suggestions } = {}) {
  const key = esc(name);
  let input;
  if (choices) {
    input = `<select name="${key}">${choices.map(([id, title]) => option(id, title, value)).join("")}</select>`;
  } else if (type === "textarea") {
    input = `<textarea name="${key}"${required ? " required" : ""}>${esc(value)}</textarea>`;
  } else if (type === "checkbox") {
    return `<label class="check ${wide ? "wide" : ""}"><input type="checkbox" name="${key}"${value ? " checked" : ""}>${esc(label)}</label>`;
  } else {
    input = `<input name="${key}" type="${type}" value="${esc(value)}"${required ? " required" : ""}${min !== undefined ? ` min="${min}"` : ""}${max !== undefined ? ` max="${max}"` : ""}${step !== undefined ? ` step="${step}"` : ""}${placeholder ? ` placeholder="${esc(placeholder)}"` : ""}${suggestions ? ` list="${esc(suggestions)}"` : ""}>`;
  }
  return `<label class="field${wide ? " wide" : ""}"><span>${esc(label)}</span>${input}</label>`;
}

function renderProperties() {
  const cue = selectedCue();
  const top = '<span class="eyebrow">Cue 属性 / 当前条目</span><h2>指令配置</h2>';
  if (!cue) {
    $("properties").innerHTML = top + '<p class="empty">选择一条 Cue 查看属性。</p>';
    return;
  }
  const commands = targets(cue);
  const template = [["", "自定义指令"], ...state.project.templates.map(item => [item.id, item.name])];
  const choices = state.project.ipGroups;
  const cards = commands.map((command, index) => {
    const available = choices.filter(group => group.id === command.ipGroupId ||
      (group.nodes.length && !commands.some(other => other.ipGroupId === group.id)));
    const selectedTemplate = state.project.templates.find(item => item.id === command.templateId);
    const response = responseFor(cue, command);
    return `<section class="cue-target" data-target="${index}">
      <div class="target-title"><b>目标组 ${index + 1}</b>${commands.length > 1 ? button("移除", "remove-target", index, "quiet") : ""}</div>
      <div class="property-form">` +
      field("目标 IP 分组", "ipGroupId", command.ipGroupId, { wide: true, choices: available.map(group => [group.id, group.name]) }) +
      field("指令模板", "templateId", command.templateId || "", { wide: true, choices: template }) +
      (selectedTemplate ? `<p class="property-help wide">模板内容 · ${esc(selectedTemplate.encoding.toUpperCase())}<br><code>${esc(selectedTemplate.payload)}</code></p>` :
        field("UDP 内容", "payload", command.payload, { wide: true, type: "textarea" }) +
        field("内容编码", "encoding", command.encoding, { choices: [["utf8", "UTF-8"], ["hex", "HEX"]] })) +
      field("UDP 端口", "port", command.port, { type: "number", min: 1, max: 65535 }) +
      field("回执模式", "responseMode", response.mode, { choices: Object.entries(replyModes) }) +
      (response.mode === "exact" ? field("预期 UDP 回执", "responseText", response.text, { wide: true }) +
        field("回执超时 (ms)", "responseTimeout", response.timeoutMs, { type: "number", min: 100, max: 30000 }) : "") +
      "</div></section>";
  }).join("");
  const canAdd = choices.some(group => group.nodes.length && !commands.some(command => command.ipGroupId === group.id));
  $("properties").innerHTML = top + '<div id="cueForm"><div class="property-form">' +
    field("Cue 名称", "name", cue.name, { wide: true, required: true }) +
    field("完成后等待 (s)", "delayAfterSeconds", cue.delayAfterMs == null ? "" : cue.delayAfterMs / 1000, {
      ...playbackSecondsInput, wide: true, placeholder: "留空沿用列表默认间隔"
    }) +
    "</div>" + cards + button("＋ 添加目标组", "add-target", "", "quiet", !canAdd) +
    '<h3 class="cue-checks">Cue 的设备检测和失败策略</h3><div class="property-form">' +
    field("发送后检查 ICMP Ping", "ping", cue.ping, { type: "checkbox", wide: true }) +
    field("失败策略", "onFail", cue.onFail, { choices: Object.entries(policies) }) +
    (cue.onFail === "retry" ? field("重试次数", "retries", cue.retries, { type: "number", min: 0, max: 5 }) : "") +
    '</div></div><p class="property-help">各目标组的指令可单独设置回执。ICMP Ping 检查设备可达性。重试仅重发失败节点。</p><div class="actions">' +
    button("移除 Cue", "delete-cue", cue.id, "quiet") + "</div>";
  if (state.access.role !== "admin")
    $("properties").querySelectorAll("input, select, textarea").forEach(input => { input.disabled = true; });
}

const resourceInfo = {
  ips: ["IP 分组库", "支持 Excel 和 TXT 批量合并。导出的文件可作模板，TXT 也可每行填写一个 IP。", "新增 IP 分组"],
  templates: ["模板库", "维护可在任意 Cue 中引用的 UDP 内容和编码。", "新增模板"],
  schedules: ["定时任务", "指定日 > 节假日 > 周循环。当天只允许最高优先级的定时任务，手动和 Action Pad 仍可执行。", "新增任务"],
  pads: ["Action Pad", "点击一个按钮以手动优先级运行指定 Cue 列表。", "新增按钮"]
};
const collections = { ips: "ipGroups", templates: "templates", schedules: "schedules", pads: "pads" };

function listLabel(id) {
  const found = lists().find(item => item.list.id === id);
  return found ? `${found.show.name} / ${found.group.name} / ${found.list.name}` : "未设置";
}

function scheduleDays(item) {
  if (item.mode === "date") return item.startDate;
  if (item.mode === "holiday") return `${item.startDate} 至 ${item.endDate}`;
  if (!item.days || item.days.length === 7) return "每天";
  return "周 " + weekdays.filter(([day]) => item.days.includes(day)).map(([, label]) => label).join("、");
}

function updateScheduleMode() {
  const indicator = $("scheduleMode");
  if (indicator)
    indicator.textContent = `今日定时 · ${scheduleModes[state.scheduleMode]}${state.timezone ? " · " + state.timezone : ""}`;
}

function renderResource() {
  const kind = state.tab;
  const info = resourceInfo[kind];
  const entries = state.project[collections[kind]];
  const edit = item => button("编辑", "edit-resource", item.id, "quiet") +
    button("移除", "delete-resource", item.id, "quiet");
  let content;
  if (kind === "pads") {
    content = '<div class="pads">' + entries.map(item => `<article class="pad">
      <button type="button" class="fire${item.action === "stopAll" ? " stop-pad" : ""}" data-action="fire-pad" data-id="${esc(item.id)}">${item.action === "stopAll" ? "■" : "▶"} ${esc(item.name)}</button>
      <p>${item.action === "stopAll" ? "全部停止" : esc(listLabel(item.listId))}</p><div class="actions">${edit(item)}</div></article>`).join("") +
      (entries.length ? "" : '<p class="empty">还没有 Action Pad 按钮。</p>') + "</div>";
  } else if (kind === "ips") {
    content = table(["分组", "节点数", "地址", "操作"], entries.map(item =>
      `<tr><td>${esc(item.name)}</td><td>${item.nodes.length}</td><td class="mono">${esc(item.nodes.slice(0, 4).map(node => node.ip).join(" · "))}${item.nodes.length > 4 ? " …" : ""}</td><td><div class="actions">${edit(item)}${button("健康查询", "edit-ip-health", item.id, "quiet", !item.nodes.length)}</div></td></tr>`).join(""));
  } else if (kind === "templates") {
    content = table(["模板", "编码", "指令内容", "操作"], entries.map(item =>
      `<tr><td>${esc(item.name)}</td><td>${badge(item.encoding.toUpperCase())}</td><td class="mono">${esc(item.payload.slice(0, 100))}</td><td><div class="actions">${edit(item)}</div></td></tr>`).join(""));
  } else {
    content = '<p id="scheduleMode" class="schedule-mode" role="status"></p>' +
      table(["任务", "模式", "日期规则", "时间", "Cue 列表", "状态", "操作"], entries.map(item =>
        `<tr><td>${esc(item.name)}</td><td>${badge(scheduleModes[item.mode ?? "weekly"])}</td><td>${esc(scheduleDays(item))}</td>
        <td class="mono">${esc(item.at)}</td><td>${esc(listLabel(item.listId))}</td><td>${badge(item.enabled ? "已启用" : "已停用", item.enabled ? "good" : "")}</td><td><div class="actions">${edit(item)}</div></td></tr>`).join(""));
  }
  const fileActions = kind === "ips" ? button("导入 Excel / TXT", "import-ips", "", "quiet", state.importingIps) +
    button("导出 Excel", "export-ips", "xlsx", "quiet") + button("导出 TXT", "export-ips", "txt", "quiet") : "";
  $("main").innerHTML = heading(info[0], info[1],
    button("＋ " + info[2], "edit-resource", "", "strong") + fileActions) + content;
  updateScheduleMode();
}

function healthRows() {
  const map = new Map();
  state.project.ipGroups.forEach(group => group.nodes.forEach(node => {
    if (!map.has(node.ip)) map.set(node.ip, { ip: node.ip, labels: [], groups: [], configured: false });
    const row = map.get(node.ip);
    row.configured ||= !!node.healthQuery;
    if (node.label && !row.labels.includes(node.label)) row.labels.push(node.label);
    row.groups.push(group);
  }));
  const statusMap = new Map(state.health.map(item => [item.ip, item]));
  return [...map.values()].map(row => ({ ...row, ...statusMap.get(row.ip), status: statusMap.get(row.ip)?.status ?? "unknown",
    softwareStatus: statusMap.get(row.ip)?.softwareStatus ?? (row.configured ? "unknown" : "notConfigured") }));
}

function visibleNodes() {
  const { search, group, status } = state.filters;
  return healthRows().filter(row =>
    (!search || (row.ip + " " + row.labels.join(" ")).toLowerCase().includes(search.toLowerCase())) &&
    (!group || row.groups.some(item => item.id === group)) && (!status || row.status === status));
}

function localTime(value) {
  return value && !value.startsWith("0001") ? new Date(value).toLocaleString("zh-CN", { hour12: false }) : "—";
}

function renderHealthRows() {
  const page = pageView(visibleNodes(), state.healthPage, state.healthPageSize);
  state.healthPage = page.current;
  const rows = page.items.map(row => `<tr><td class="mono">${esc(row.ip)}</td><td>${esc(row.labels.join(" / ") || "—")}</td>
    <td>${esc(row.groups.map(group => group.name).join(" / "))}</td><td>${badge(healthLabels[row.status], row.status === "online" ? "good" : row.status === "offline" ? "bad" : "")}</td>
    <td>${badge(softwareLabels[row.softwareStatus], row.softwareStatus === "online" ? "good" : row.softwareStatus === "unconfirmed" ? "warn" : "")}</td>
    <td class="mono">${row.latencyMs == null ? "—" : `${row.latencyMs} ms`}</td><td class="mono">${esc(localTime(row.checkedAt))}</td></tr>`).join("");
  $("healthTable").innerHTML = table(["IP 地址", "节点", "所属分组", "设备状态", "软件状态", "Ping 延迟", "最后检测时间"], rows, "没有符合条件的节点。");
  $("healthPager").innerHTML = pageControls(page, "个节点") +
    `<select id="healthPageSize" aria-label="每页节点数">${[25, 50, 100].map(size => option(size, `${size} 条 / 页`, state.healthPageSize)).join("")}</select>`;
  const all = healthRows();
  const cards = [["节点总数", all.length, ""], ...Object.entries(healthLabels).map(([key, label]) =>
    ["设备" + label, all.filter(row => row.status === key).length, key === "online" ? "good" : key === "offline" ? "bad" : ""])];
  $("healthStats").innerHTML = cards.map(([label, count, type]) => `<div class="stat ${type}"><span>${label}</span><b>${count}</b></div>`).join("");
}

function renderHealthProgress() {
  const indicator = $("healthProgress");
  if (!indicator) return;
  const seconds = state.project.healthPollSeconds ?? 0;
  const scan = state.healthScan;
  const label = (seconds ? `自动检测 · 每 ${seconds} 秒` : "手动检测 · 自动检测关闭") +
    (scan?.inProgress ? " · 检测中…" : scan?.lastCompletedAt ? ` · 最近完成 ${localTime(scan.lastCompletedAt)}` : "");
  if (indicator.textContent !== label) indicator.textContent = label;
}

async function syncHealthScan(scan) {
  state.healthScan = scan;
  renderHealthProgress();
  if (state.tab !== "health" || !state.access.role || !scan || scan.version === state.healthVersion || state.healthFetching) return;
  state.healthFetching = true;
  try {
    state.health = await request("/health");
    state.healthVersion = scan.version;
    if (state.tab === "health" && $("healthTable")) renderHealthRows();
  } catch { /* A later status poll can retry. */ }
  finally { state.healthFetching = false; }
}

function renderHealth() {
  $("main").innerHTML = heading("节点看板", "设备状态看 Ping，软件状态看可选的专用 UDP 查询回执。",
    button("刷新状态", "refresh-health", "", "strong") + button("检测间隔", "edit-health-interval", "", "quiet") +
    button("导出 CSV", "export-health")) +
    `<p id="healthProgress" class="schedule-mode" role="status"></p><div class="stats" id="healthStats"></div>
      <div class="filters"><input aria-label="搜索 IP 或节点" id="healthSearch" type="search" placeholder="搜索 IP 或节点" value="${esc(state.filters.search)}">
      <select aria-label="筛选分组" id="healthGroup">${option("", "全部分组")}${state.project.ipGroups.map(group => option(group.id, group.name, state.filters.group)).join("")}</select>
      <select aria-label="筛选状态" id="healthStatus">${option("", "全部状态")}${Object.entries(healthLabels).map(([key, name]) => option(key, name, state.filters.status)).join("")}</select></div>
      <div id="healthTable"></div><div id="healthPager" class="pager"></div>`;
  renderHealthProgress();
  renderHealthRows();
}

function exportHealth() {
  const rows = [["IP 地址", "节点", "分组", "设备状态", "软件状态", "Ping 延迟 (ms)", "最后检测时间"], ...visibleNodes().map(row =>
    [row.ip, row.labels.join(" / "), row.groups.map(group => group.name).join(" / "),
      healthLabels[row.status], softwareLabels[row.softwareStatus], row.latencyMs ?? "", localTime(row.checkedAt)])];
  downloadCsv("节点状态.csv", rows);
}

function downloadCsv(name, rows) {
  const cell = value => {
    const string = String(value);
    return '"' + (/^(?:[\t\r\n]|\s*[=+\-@])/.test(string) ? "'" : "") + string.replaceAll('"', '""') + '"';
  };
  downloadFile(name, new Blob(["\uFEFF", rows.map(row => row.map(cell).join(",")).join("\r\n")], { type: "text/csv;charset=utf-8" }));
}

function downloadFile(name, blob) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

async function importIpLibrary(file) {
  if (state.importingIps) return;
  state.importingIps = true;
  const project = state.project;
  const original = JSON.stringify(project.ipGroups);
  const form = new FormData();
  form.set("file", file);
  form.set("groups", original);
  try {
    const groups = await request("/ip-library/import", "POST", form);
    if ($("editor").open || state.project !== project || JSON.stringify(project.ipGroups) !== original)
      throw new Error("导入期间 IP 分组库已更改。请重新选择文件导入。");
    project.ipGroups = groups;
    dirty();
    render();
    notice("已合并到工程草稿，核对后点击保存项目。");
  } finally {
    state.importingIps = false;
    if (state.tab === "ips") {
      const button = $("main").querySelector('[data-action="import-ips"]');
      if (button) button.disabled = false;
    }
  }
}

async function exportIpLibrary(format) {
  const response = await fetch("/api/ip-library/export/" + format, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify(state.project.ipGroups)
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.message || `导出失败 (${response.status})`);
  }
  downloadFile("IP分组库." + format, await response.blob());
}

function edit(title, fields, submit, remove, markDirty = true) {
  state.editor = submit;
  state.editorMarksDirty = markDirty;
  $("editTitle").textContent = title;
  $("fields").innerHTML = fields + (remove ? button("移除此项", "delete-current", "", "danger") : "");
  state.editorRemove = remove;
  $("formError").textContent = "";
  $("editor").showModal();
}

function named(label, value, apply, remove) {
  edit(label, field("名称", "name", value, { required: true }), data => apply(requiredName(data)), remove);
}

function requiredName(data) {
  const name = data.get("name")?.trim();
  if (!name || name.length > 80) throw new Error("名称须为 1–80 个字符。");
  return name;
}

function removeHierarchy(collection, item, listIds) {
  if ([...state.project.schedules, ...state.project.pads].some(ref => listIds.includes(ref.listId)))
    throw new Error("请先移除引用该 Cue 列表的定时任务和 Action Pad 按钮。");
  if (confirm(`移除「${item.name}」及其下属条目？`))
    collection.splice(collection.indexOf(item), 1);
  else return false;
  return true;
}

function scheduleFields(mode, item) {
  if (mode === "weekly") {
    const days = item?.days ?? weekdays.map(([day]) => day);
    return '<fieldset class="weekdays"><legend>每周执行日</legend>' +
      weekdays.map(([day, label]) => `<label class="check"><input type="checkbox" name="day" value="${day}"${days.includes(day) ? " checked" : ""}>${label}</label>`).join("") +
      "</fieldset>";
  }
  return field(mode === "date" ? "指定日期" : "起始日期", "startDate", item?.startDate, { type: "date", required: true }) +
    (mode === "holiday" ? field("结束日期（含当天）", "endDate", item?.endDate, { type: "date", required: true }) : "");
}

function editResource(id) {
  const kind = state.tab;
  const entries = state.project[collections[kind]];
  const item = entries.find(row => row.id === id);
  const initial = field("名称", "name", item?.name, { required: true });
  let fields = initial;
  if (kind === "ips") {
    fields += field("节点（每行 IP, 名称, 模拟：ok / pingFail / timeout）", "nodes",
      item?.nodes.map(node => [node.ip, node.label, node.simulation].join(", ")).join("\n"), { type: "textarea" });
  } else if (kind === "templates") {
    fields += field("内容编码", "encoding", item?.encoding ?? "utf8", { choices: [["utf8", "UTF-8"], ["hex", "HEX"]] }) +
      field("UDP 指令", "payload", item?.payload, { type: "textarea", required: true });
  } else {
    if (kind === "pads")
      fields += field("按钮动作", "padAction", item?.action ?? "play", { choices: [["play", "播放 Cue 列表"], ["stopAll", "全部停止"]] });
    fields += `<div id="targetListField"${item?.action === "stopAll" ? " hidden" : ""}>` +
      field("目标 Cue 列表", "listId", item?.listId, { choices: lists().map(row => [row.list.id, listLabel(row.list.id)]) }) + "</div>";
    if (kind === "schedules") {
      const mode = item?.mode ?? "weekly";
      fields += field("定时模式", "mode", mode, { choices: Object.entries(scheduleModes) }) +
        field("执行时间", "at", item?.at ?? "19:00", { type: "time", required: true }) +
        '<div id="scheduleFields" class="property-form wide">' + scheduleFields(mode, item) + "</div>" +
        field("启用任务", "enabled", item?.enabled ?? false, { type: "checkbox" });
    }
  }
  edit(item ? "编辑 · " + item.name : resourceInfo[kind][2], fields, data => {
    const next = { id: item?.id ?? ids(), name: requiredName(data) };
    if (kind === "ips") {
      next.nodes = data.get("nodes").split(/\r?\n/).filter(line => line.trim()).map(line => {
        const [ip, label = "", simulation = "ok", ...extra] = line.split(",").map(value => value.trim());
        if (!ip || extra.length || !["ok", "pingFail", "timeout"].includes(simulation))
          throw new Error("节点格式为 IP, 名称, 模拟。模拟值可省略。");
        return { ip, label, simulation, healthQuery: item?.nodes.find(node => node.ip === ip)?.healthQuery ?? null };
      });
    } else if (kind === "templates") {
      Object.assign(next, { payload: data.get("payload"), encoding: data.get("encoding") });
    } else {
      next.listId = data.get("listId");
      if (kind === "pads") {
        next.action = data.get("padAction");
        if (next.action === "stopAll") next.listId = "";
      }
      if (next.action !== "stopAll" && !next.listId) throw new Error("请选择目标 Cue 列表。");
      if (kind === "schedules") {
        const mode = data.get("mode");
        const days = mode === "weekly" ? data.getAll("day").map(Number) : null;
        const startDate = mode === "weekly" ? null : data.get("startDate");
        const endDate = mode === "holiday" ? data.get("endDate") : null;
        if (days && !days.length) throw new Error("请至少选择一个执行日。");
        if (mode === "holiday" && endDate < startDate) throw new Error("结束日期必须在起始日期当天或之后。");
        Object.assign(next, { mode, days, startDate, endDate, at: data.get("at"), enabled: data.has("enabled") });
      }
    }
    if (item) Object.assign(item, next);
    else entries.push(next);
  });
  if (kind === "schedules") {
    $("fields").querySelector('[name="mode"]').addEventListener("change", event => {
      $("scheduleFields").innerHTML = scheduleFields(event.target.value, item);
    });
  }
  if (kind === "pads") {
    $("fields").querySelector('[name="padAction"]').addEventListener("change", event => {
      $("targetListField").hidden = event.target.value === "stopAll";
    });
  }
}

function editIpHealth(id) {
  const group = state.project.ipGroups.find(item => item.id === id);
  if (!group?.nodes.length) return;
  const allNodes = state.project.ipGroups.flatMap(item => item.nodes);
  const queryFor = ip => allNodes.find(node => node.ip === ip && node.healthQuery)?.healthQuery;
  const options = ip => {
    const query = queryFor(ip);
    return field("健康模式", "healthMode", query ? "udp" : "ping", {
      choices: [["ping", "仅 Ping"], ["udp", "Ping + UDP 软件回执"]]
    }) + `<div id="queryFields" class="property-form"${query ? "" : " hidden"}>` +
      field("查询端口", "port", query?.port, { type: "number", min: 1, max: 65535 }) +
      field("预期回执", "reply", query?.reply ?? "IMOK") +
      field("专用查询指令（UTF-8）", "payload", query?.payload ?? "ping", { type: "textarea", wide: true }) + "</div>";
  };
  edit("节点健康查询", field("IP 地址", "ip", group.nodes[0].ip, {
    choices: group.nodes.map(node => [node.ip, node.label ? `${node.ip} · ${node.label}` : node.ip])
  }) + '<div id="healthQueryOptions">' + options(group.nodes[0].ip) +
    '</div><p class="property-help">使用设备协议约定的状态查询指令。设置应用到所有包含该 IP 的分组。</p>', data => {
      const ip = data.get("ip");
      let query = null;
      if (data.get("healthMode") === "udp") {
        const port = Number(data.get("port"));
        const payload = data.get("payload");
        const reply = data.get("reply");
        if (!Number.isInteger(port) || port < 1 || port > 65535 || !payload.trim() || !reply.trim())
          throw new Error("请填写有效的端口、专用查询指令和预期回执。");
        query = { port, payload, reply };
      }
      allNodes.filter(node => node.ip === ip).forEach(node => { node.healthQuery = query; });
    });
  $("fields").querySelector('[name="ip"]').addEventListener("change", event => {
    $("healthQueryOptions").innerHTML = options(event.target.value);
  });
  $("healthQueryOptions").addEventListener("change", event => {
    if (event.target.name === "healthMode") $("queryFields").hidden = event.target.value !== "udp";
  });
}

function deleteResource(id) {
  const kind = state.tab;
  const entries = state.project[collections[kind]];
  const item = entries.find(entry => entry.id === id);
  const key = kind === "ips" ? "ipGroupId" : "templateId";
  if ((kind === "ips" || kind === "templates") &&
      lists().some(row => row.list.cues.some(cue => targets(cue).some(target => target[key] === id))))
    throw new Error("该资源仍被 Cue 引用。");
  if (confirm(`移除「${item.name}」？`)) {
    entries.splice(entries.indexOf(item), 1);
    dirty();
    render();
  }
}

async function run(path) {
  if (state.stoppingAll) throw new Error("正在停止全部播放。");
  if (state.dirty) throw new Error("先保存项目，再执行当前配置。");
  const result = await request(path, "POST");
  notice(result.message);
  await poll();
}

async function stopRun(active) {
  if (!active || isStopping(active)) return;
  state.stoppingRuns.add(active.id);
  updateTransport();
  try {
    const result = await request("/stop/" + active.id, "POST");
    notice(result.stopped ? "已请求停止本次执行。" : "本次执行已经结束。");
    await poll();
  } finally {
    state.stoppingRuns.delete(active.id);
    updateTransport();
  }
}

async function stopAll() {
  if (state.stoppingAll || !state.runs.length) return;
  state.stoppingAll = true;
  updateTransport();
  try {
    const result = await request("/stop", "POST");
    notice(result.stopped ? `已请求停止 ${result.count ?? state.runs.length} 个播放列表。` : "所有播放均已结束。");
    await poll();
  } finally {
    state.stoppingAll = false;
    updateTransport();
  }
}

async function act(action, id, target) {
  const project = state.project;
  const current = selected();
  if (action === "rename-project") return named("项目名称", project.name, name => { project.name = name; });
  if (action === "select-list") {
    state.listId = id;
    state.cueId = null;
    state.tab = "cues";
  } else if (action === "select-cue") {
    if (target.dataset.listId) state.listId = target.dataset.listId;
    state.cueId = id;
    state.tab = "cues";
  } else if (action === "edit-show") {
    const show = project.shows.find(item => item.id === id);
    return named("演出设置", show.name, name => { show.name = name; }, () =>
      removeHierarchy(project.shows, show, show.groups.flatMap(group => group.lists.map(list => list.id))));
  } else if (action === "add-group") {
    const show = project.shows.find(item => item.id === id);
    return named("新增分组", "", name => {
      const list = { id: ids(), name: name + " · 主播放", cues: [] };
      show.groups.push({ id: ids(), name, lists: [list] });
      state.listId = list.id;
      state.tab = "cues";
    });
  } else if (action === "edit-group" || action === "add-list") {
    const show = project.shows.find(show => show.groups.some(group => group.id === id));
    const group = show.groups.find(group => group.id === id);
    if (action === "edit-group")
      return named("分组设置", group.name, name => { group.name = name; }, () =>
        removeHierarchy(show.groups, group, group.lists.map(list => list.id)));
    return named("新增 Cue 列表", "", name => {
      const list = { id: ids(), name, cues: [] };
      group.lists.push(list);
      state.listId = list.id;
      state.tab = "cues";
    });
  } else if (action === "edit-list") {
    const { list, group } = current;
    const knownGroups = [...new Set(lists().map(item => item.list.playbackGroup ?? "默认"))].sort();
    return edit("Cue 列表设置",
      field("名称", "name", list.name, { required: true }) +
      field("播放组", "playbackGroup", list.playbackGroup ?? "默认", { required: true, suggestions: "playbackGroups" }) +
      `<datalist id="playbackGroups">${knownGroups.map(groupName => option(groupName, groupName)).join("")}</datalist>` +
      '<p class="property-help">同组触发新列表会切换播放，不同组可以同时播放。</p>' +
      field("播放模式", "playbackMode", list.playbackMode ?? "once", { choices: Object.entries(playbackModes) }) +
      field("默认间隔 (s)", "intervalSeconds", (list.intervalMs ?? 0) / 1000, {
        ...playbackSecondsInput, required: true
      }) +
      '<p class="property-help">首条立即播放。每条 Cue 执行结束后开始计时。循环每轮的等待时间合计至少 0.1 秒。</p>',
      data => {
        const name = requiredName(data);
        const playbackGroup = data.get("playbackGroup").trim();
        if (!playbackGroup || playbackGroup.length > 80) throw new Error("播放组名称须为 1–80 个字符。");
        const playbackMode = data.get("playbackMode");
        const intervalMs = Math.round(Number(data.get("intervalSeconds")) * 1000);
        if (playbackMode === "loop" && list.cues.length &&
            list.cues.reduce((total, cue) => total + (cue.delayAfterMs ?? intervalMs), 0) < 100)
          throw new Error("循环播放每轮至少需要 0.1 秒的等待时间。");
        Object.assign(list, { name, playbackGroup, playbackMode, intervalMs });
      },
      () => removeHierarchy(group.lists, list, [list.id]));
  } else if (action === "add-cue") {
    const group = project.ipGroups.find(item => item.nodes.length);
    if (!group) throw new Error("请先在 IP 分组库添加至少一个节点。");
    const cue = { id: ids(), name: "新 Cue", ipGroupId: group.id, templateId: null, payload: "SCENE START",
      encoding: "utf8", port: 7000, ping: false, reply: "IMOK", timeoutMs: 1800, onFail: "stop", retries: 0,
      response: { mode: "none" } };
    current.list.cues.push(cue);
    state.cueId = cue.id;
    dirty();
  } else if (action === "add-target" || action === "remove-target") {
    const cue = selectedCue();
    const commands = targets(cue);
    if (action === "add-target") {
      const group = project.ipGroups.find(item => item.nodes.length && !commands.some(command => command.ipGroupId === item.id));
      if (!group) throw new Error("请先创建另一个包含节点的 IP 分组。");
      const { templateId, payload, encoding, port } = commands.at(-1);
      (cue.additionalTargets ??= []).push({ ipGroupId: group.id, templateId, payload, encoding, port, response: { mode: "none" } });
    } else {
      const index = Number(id);
      if (commands.length <= 1 || index < 0 || index >= commands.length) return;
      if (index === 0) {
        const replacement = cue.additionalTargets.shift();
        Object.assign(cue, replacement, { response: responseFor(cue, replacement) });
      }
      else cue.additionalTargets.splice(index - 1, 1);
    }
    dirty();
  } else if (action === "delete-cue") {
    const index = current.list.cues.findIndex(cue => cue.id === id);
    if (!confirm(`移除「${current.list.cues[index].name}」？`)) return;
    current.list.cues.splice(index, 1);
    dirty();
  } else if (action === "up" || action === "down") {
    const cues = current.list.cues;
    const from = cues.findIndex(cue => cue.id === id);
    const to = from + (action === "up" ? -1 : 1);
    [cues[from], cues[to]] = [cues[to], cues[from]];
    dirty();
  } else if (action === "edit-resource") return editResource(id);
  else if (action === "delete-resource") return deleteResource(id);
  else if (action === "run" || action === "tree-play") return run("/run/" + id);
  else if (action === "stop-selected") {
    return stopRun(state.runs.findLast(run => run.listId === id));
  }
  else if (action === "fire-pad") return project.pads.find(pad => pad.id === id)?.action === "stopAll" ? stopAll() : run("/pad/" + id);
  else if (action === "edit-ip-health") return editIpHealth(id);
  else if (action === "edit-health-interval") {
    return edit("节点检测间隔", field("间隔（秒，0 为手动检测）", "seconds", project.healthPollSeconds ?? 0,
      { type: "number", min: 0, max: 3600, required: true }) +
      '<p class="property-help">设为 5–3600 秒后保存工程，后台会在上一轮完成后等候该间隔再检测。设置为 0 时使用「刷新状态」。</p>',
      data => {
        const seconds = Number(data.get("seconds"));
        if (!Number.isInteger(seconds) || (seconds !== 0 && (seconds < 5 || seconds > 3600)))
          throw new Error("请输入 0 或 5–3600 秒。");
        project.healthPollSeconds = seconds;
      });
  }
  else if (action === "refresh-health") {
    if (state.dirty) throw new Error("先保存项目，再检测已配置节点。");
    target.disabled = true;
    try {
      state.health = await request("/health/refresh", "POST");
      if (state.tab === "health") renderHealth();
      notice("节点健康检测完成。");
    } finally { target.disabled = false; }
    return;
  } else if (action === "export-health") return exportHealth();
  else if (action === "import-ips") {
    if (!state.importingIps) $("ipImportFile").click();
    return;
  } else if (action === "export-ips") return exportIpLibrary(id);
  else if (action === "delete-current") {
    if (!state.editorRemove?.()) return;
    $("editor").close();
    dirty();
  }
  render();
}

async function load() {
  const data = await request("/project");
  state.project = data.project;
  state.revision = data.revision;
  state.dirty = false;
  [state.health, state.layout] = await Promise.all([request("/health"), request("/layout")]);
  if (Array.isArray(state.layout)) state.layout = { order: state.layout, sizes: {} };
  render();
}

async function poll() {
  const version = ++statusRequest;
  let data;
  try {
    data = await request("/status");
  } catch (error) {
    if (version === statusRequest) {
      state.countdowns.clear();
      updateCountdown();
    }
    throw error;
  }
  if (version !== statusRequest) return;
  if (data.access) {
    const changed = state.access.role !== data.access.role;
    applyAccess(data.access);
    if (changed && data.access.role) await load();
    if (version !== statusRequest) return;
    $("lockStatus").textContent = `引擎继续运行 · ${data.activeCount ?? 0} 个列表 · ${data.live ? "真实 UDP" : "模拟模式"}`;
  }
  state.runs = data.runs ?? (data.active ? [data.active] : []);
  const countdowns = new Map();
  for (const run of state.runs) {
    if (!run.nextCueName || !Number.isFinite(run.remainingWaitMs)) continue;
    const key = `${run.round}/${run.cueId}`;
    const deadline = performance.now() + run.remainingWaitMs;
    const previous = state.countdowns.get(run.id);
    countdowns.set(run.id, { key, nextCue: run.nextCueName,
      deadline: previous?.key === key ? Math.min(previous.deadline, deadline) : deadline });
  }
  state.countdowns = countdowns;
  state.timezone = data.timeZone;
  state.scheduleMode = data.scheduleMode ?? "weekly";
  syncHealthScan(data.healthScan ?? null);
  $("logStorageWarning").hidden = !data.logStorageWarning;
  updateScheduleMode();
  $("mode").classList.toggle("live", data.live);
  $("mode").innerHTML = data.live ? "<i></i>真实 UDP" : "<i></i>模拟模式";
  updateTransport();
  $("logs").innerHTML = data.events.map(event => `<tr><td class="mono">${esc(new Date(event.at).toLocaleTimeString("zh-CN", { hour12: false }))}</td>
    <td>${esc(sourceLabels[event.source] ?? event.source)}</td><td>${esc([event.list, event.cue].filter(Boolean).join(" / "))}</td>
    <td>${badge(eventLabels[event.status] ?? event.status, ["success", "ack", "completed"].includes(event.status) ? "good" :
      ["failed", "stopped"].includes(event.status) ? "bad" : ["retry", "skipped", "priority", "adapterWait"].includes(event.status) ? "warn" : "")}</td><td>${esc(event.message)}</td></tr>`).join("") ||
    '<tr><td colspan="5">等待首条执行记录</td></tr>';
}

document.addEventListener("click", async event => {
  const healthPage = event.target.closest("#healthPager [data-page-step]");
  if (healthPage && state.access.role) {
    state.healthPage += Number(healthPage.dataset.pageStep);
    renderHealthRows();
    return;
  }
  const tab = event.target.closest("[data-tab]");
  const trigger = event.target.closest("[data-action]");
  if (!state.project || !state.access.role || (!tab && !trigger)) return;
  try {
    if (tab) {
      state.tab = tab.dataset.tab;
      if (state.tab === "health") state.health = await request("/health");
      render();
    } else {
      event.preventDefault();
      if (adminActions.has(trigger.dataset.action) && state.access.role !== "admin") return;
      await act(trigger.dataset.action, trigger.dataset.id, trigger);
    }
  } catch (error) { notice(error.message, true); }
});

document.addEventListener("input", event => {
  const target = event.target;
  if (target.closest("#cueForm")) {
    const cue = selectedCue();
    if (target.name === "delayAfterSeconds") {
      cue.delayAfterMs = target.value === "" ? null : Math.round(target.valueAsNumber * 1000);
      document.querySelector("#main tr.selected .cue-delay").textContent = cueWaitLabel(selected().list, cue);
      dirty();
      return;
    }
    const groupCard = target.closest("[data-target]");
    const entry = groupCard ? targets(cue)[Number(groupCard.dataset.target)] : cue;
    if (groupCard && ["responseMode", "responseText", "responseTimeout"].includes(target.name)) {
      const response = { ...responseFor(cue, entry) };
      if (target.name === "responseMode") response.mode = target.value;
      else if (target.name === "responseText") response.text = target.value;
      else response.timeoutMs = target.valueAsNumber || 0;
      entry.response = response;
      dirty();
      return;
    }
    entry[target.name] = target.type === "checkbox" ? target.checked : target.type === "number"
      ? (target.valueAsNumber || 0) : target.name === "templateId" ? (target.value || null) : target.value;
    dirty();
    if (target.name === "name") {
      document.querySelector("#main tr.selected .row-select").textContent = cue.name;
      const link = document.querySelector("#tree .cue-link.selected");
      if (link) link.textContent = "· " + cue.name;
    }
  } else if (target.id === "healthSearch") {
    state.filters.search = target.value;
    state.healthPage = 1;
    renderHealthRows();
  }
});

document.addEventListener("change", event => {
  const target = event.target;
  if (target.closest("#cueForm") && ["templateId", "onFail", "ipGroupId", "ping", "responseMode"].includes(target.name)) {
    renderCues();
  } else if (target.id === "healthGroup" || target.id === "healthStatus") {
    state.filters[target.id === "healthGroup" ? "group" : "status"] = target.value;
    state.healthPage = 1;
    renderHealthRows();
  } else if (target.id === "healthPageSize") {
    state.healthPageSize = Number(target.value);
    state.healthPage = 1;
    renderHealthRows();
  }
});

$("addShow").addEventListener("click", () => {
  if (!state.project) return;
  named("新增演出", "", name => {
    state.project.shows.push({ id: ids(), name, groups: [] });
    if (!$("projectSearch").value.trim()) state.treePage = Math.ceil(state.project.shows.length / 8);
  });
});
$("projectSearch").addEventListener("input", () => {
  if (!state.project || !state.access.role) return;
  state.treePage = 1;
  renderTree();
  updateTransport();
});
$("treePager").addEventListener("click", event => {
  const button = event.target.closest("[data-page-step]");
  if (!button || !state.access.role) return;
  state.treePage += Number(button.dataset.pageStep);
  renderTree();
  updateTransport();
});
$("closeEditor").addEventListener("click", () => $("editor").close());
$("cancelEdit").addEventListener("click", () => $("editor").close());
$("editForm").addEventListener("submit", async event => {
  event.preventDefault();
  const submit = event.target.querySelector('[type="submit"]');
  submit.disabled = true;
  try {
    const markDirty = state.editorMarksDirty;
    await state.editor(new FormData(event.target));
    $("editor").close();
    $("fields").replaceChildren();
    if (markDirty) { dirty(); render(); }
  } catch (error) { $("formError").textContent = error.message; }
  finally { submit.disabled = false; }
});
$("save").addEventListener("click", async () => {
  state.saving = true;
  const counter = state.editCounter;
  updateSave();
  try {
    const data = await request("/project", "PUT", { revision: state.revision, project: state.project });
    state.revision = data.revision;
    state.dirty = state.editCounter !== counter;
    notice("项目已保存。");
  } catch (error) { notice(error.message, true); }
  finally { state.saving = false; updateSave(); }
});
$("reload").addEventListener("click", async () => {
  if (state.saving) return;
  if (state.dirty && !confirm("重新载入将丢弃未保存的更改。继续？")) return;
  try { await load(); notice("已重新载入项目。"); }
  catch (error) { notice(error.message, true); }
});
$("stop").addEventListener("click", async () => {
  try { await stopAll(); }
  catch (error) { notice(error.message, true); }
});
$("ipImportFile").addEventListener("change", async event => {
  const file = event.target.files[0];
  event.target.value = "";
  if (!file) return;
  try { await importIpLibrary(file); }
  catch (error) { notice(error.message, true); }
});
$("logFolder").addEventListener("click", async () => {
  try {
    const logs = await request("/log-folder");
    if (!state.access.role) return;
    $("logFolderPath").value = logs.directory;
    $("logFolderError").textContent = [logs.error, logs.lostEntries ? `有 ${logs.lostEntries} 条记录未能写入文件。` : ""].filter(Boolean).join(" · ");
    $("logFolderDialog").showModal();
  }
  catch (error) { notice(error.message, true); }
});
$("permissions").addEventListener("click", () => { if (state.access.role === "admin") editPermissions(); });
$("udpAdapter").addEventListener("click", async () => {
  try { await editUdpAdapter(); }
  catch (error) { notice(error.message, true); }
});
$("lock").addEventListener("click", async () => {
  try {
    if (state.dirty || state.saving) throw new Error("请先保存或重新载入工程，再锁屏。");
    if (!state.access.configured) editPermissions(true);
    else await lockAccess();
  } catch (error) { notice(error.message, true); }
});
$("unlockForm").addEventListener("submit", async event => {
  event.preventDefault();
  const form = event.target;
  const submit = form.querySelector('[type="submit"]');
  submit.disabled = true;
  $("unlockError").textContent = "";
  try {
    applyAccess(await request("/access/unlock", "POST", Object.fromEntries(new FormData(form))));
    form.reset();
    await load();
    await poll();
  } catch (error) { $("unlockError").textContent = error.message; }
  finally { submit.disabled = false; }
});
$("resetLayout").addEventListener("click", () => saveLayout(defaultLayout));
const panelGrid = $("panelGrid");
for (const panel of panelGrid.children) {
  for (const [edge, label] of [["left", "左"], ["right", "右"], ["top", "上"], ["bottom", "下"]]) {
    const handle = document.createElement("button");
    handle.type = "button";
    handle.className = "panel-edge admin-only";
    handle.dataset.edge = edge;
    handle.setAttribute("aria-label", `调整${panel.getAttribute("aria-label") ?? "执行日志"}的${label}边`);
    handle.title = `拖动${label}边调整尺寸，或按方向键`;
    panel.append(handle);
  }
}
let resizeGesture = null;
panelGrid.addEventListener("pointerdown", event => {
  const handle = event.target.closest(".panel-resizer, .panel-edge");
  if (!handle || event.button !== 0 || state.access.role !== "admin" || state.layoutSaving || resizeGesture) return;
  event.preventDefault();
  const panel = handle.closest("[data-panel]");
  handle.setPointerCapture(event.pointerId);
  resizeGesture = { context: resizeContext(panel, handle.dataset.edge),
    x: event.clientX, y: event.clientY, pointerId: event.pointerId, preview: null };
});
panelGrid.addEventListener("pointermove", event => {
  const gesture = resizeGesture;
  if (gesture?.pointerId !== event.pointerId) return;
  const edge = gesture.context.edge;
  const dx = edge === "top" || edge === "bottom" ? 0 : event.clientX - gesture.x;
  const dy = edge === "left" || edge === "right" ? 0 : event.clientY - gesture.y;
  gesture.preview = dx || dy ? resizeFromEdge(gesture.context, dx, dy) : null;
  applyLayout(gesture.preview ?? state.layout);
});
function finishResize(event) {
  const gesture = resizeGesture;
  if (gesture?.pointerId !== event.pointerId) return;
  resizeGesture = null;
  if (event.type === "pointerup" && gesture.preview) saveLayout(gesture.preview);
  else applyLayout();
}
panelGrid.addEventListener("pointerup", finishResize);
panelGrid.addEventListener("pointercancel", finishResize);
panelGrid.addEventListener("lostpointercapture", finishResize);
panelGrid.addEventListener("dragstart", event => {
  const handle = event.target.closest(".dock-handle");
  if (!handle || state.access.role !== "admin") { event.preventDefault(); return; }
  event.dataTransfer.setData("application/x-showcontrol-panel", handle.closest("[data-panel]").dataset.panel);
  event.dataTransfer.effectAllowed = "move";
});
panelGrid.addEventListener("dragover", event => {
  const panel = event.target.closest("[data-panel]");
  if (!panel || !event.dataTransfer.types.includes("application/x-showcontrol-panel")) return;
  event.preventDefault();
  panelGrid.querySelector(".dock-hover")?.classList.remove("dock-hover");
  panel.classList.add("dock-hover");
});
panelGrid.addEventListener("drop", event => {
  event.preventDefault();
  panelGrid.querySelector(".dock-hover")?.classList.remove("dock-hover");
  swapPanels(event.dataTransfer.getData("application/x-showcontrol-panel"), event.target.closest("[data-panel]")?.dataset.panel);
});
panelGrid.addEventListener("dragend", () => panelGrid.querySelector(".dock-hover")?.classList.remove("dock-hover"));
panelGrid.addEventListener("keydown", async event => {
  if (event.target.matches(".panel-resizer, .panel-edge") && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) {
    const panel = event.target.closest("[data-panel]");
    const edge = event.target.dataset.edge;
    const dx = event.key === "ArrowLeft" ? -20 : event.key === "ArrowRight" ? 20 : 0;
    const dy = event.key === "ArrowUp" ? -20 : event.key === "ArrowDown" ? 20 : 0;
    if (((edge === "top" || edge === "bottom") && dx) || ((edge === "left" || edge === "right") && dy)) return;
    event.preventDefault();
    await saveLayout(resizeFromEdge(resizeContext(panel, edge), dx, dy));
    return;
  }
  if (!event.target.matches(".dock-handle") || !["ArrowLeft", "ArrowRight"].includes(event.key)) return;
  event.preventDefault();
  const handle = event.target;
  const panel = handle.closest("[data-panel]").dataset.panel;
  const visible = [...panelGrid.children].filter(item => !item.hidden).map(item => item.dataset.panel);
  await swapPanels(panel, visible[visible.indexOf(panel) + (event.key === "ArrowLeft" ? -1 : 1)]);
  handle.focus();
});
window.addEventListener("beforeunload", event => {
  if (state.dirty) { event.preventDefault(); event.returnValue = ""; }
});

const localClock = $("localClock");
const clockFormat = new Intl.DateTimeFormat("zh-CN", {
  hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
});
function updateLocalClock() {
  const time = clockFormat.format(new Date());
  localClock.textContent = time;
  localClock.dateTime = time;
}
updateLocalClock();
setInterval(updateLocalClock, 1000);

async function start() {
  try {
    applyAccess(await request("/access"));
    if (state.access.role) await load();
    await poll();
  } catch (error) { notice(error.message, true); }
  setInterval(() => poll().catch(() => {
    $("mode").textContent = "连接中断";
  }), 950);
  setInterval(updateCountdown, 100);
}
start();
