"use strict";
// Sloomy Tweaks license administration UI. Talks only to /admin/api with the X-Admin-Key header.
// All server data is inserted with textContent (never innerHTML) to avoid script injection.

const $ = (id) => document.getElementById(id);
let adminKey = sessionStorage.getItem("sloomyAdminKey");
let licenses = [];
let selected = null;

async function api(method, path, body) {
  const res = await fetch("/admin/api" + path, {
    method,
    headers: { "X-Admin-Key": adminKey, ...(body ? { "Content-Type": "application/json" } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401) { logout(); throw new Error("Session expired or invalid admin key."); }
  if (res.status === 204) return null;
  const data = await res.json().catch(() => null);
  if (!res.ok) throw new Error((data && data.message) || `Request failed (${res.status})`);
  return data;
}

function toast(msg) {
  const t = $("toast"); t.textContent = msg; t.hidden = false;
  clearTimeout(toast.timer); toast.timer = setTimeout(() => (t.hidden = true), 3500);
}

function cell(text, cls) {
  const td = document.createElement("td");
  if (text instanceof Node) td.appendChild(text); else td.textContent = text ?? "—";
  if (cls) td.className = cls;
  return td;
}

const fmt = (d) => (d ? new Date(d).toLocaleString() : "—");
const fmtDate = (d) => (d ? new Date(d).toLocaleDateString() : "—");

async function login() {
  adminKey = $("adminKey").value.trim();
  try {
    await api("GET", "/customers");
    sessionStorage.setItem("sloomyAdminKey", adminKey);
    $("login").hidden = true; $("app").hidden = false;
    await loadLicenses();
  } catch (e) {
    $("loginError").textContent = e.message; $("loginError").hidden = false;
  }
}

function logout() {
  sessionStorage.removeItem("sloomyAdminKey"); adminKey = null;
  $("app").hidden = true; $("login").hidden = false;
}

async function loadLicenses() {
  licenses = await api("GET", "/licenses");
  renderLicenses();
}

function renderLicenses() {
  const filter = $("licenseFilter").value.trim().toLowerCase();
  const rows = $("licenseRows"); rows.replaceChildren();
  for (const l of licenses) {
    const hay = [l.id, l.customerEmail, l.status, l.plan].join(" ").toLowerCase();
    if (filter && !hay.includes(filter)) continue;
    const tr = document.createElement("tr");
    const id = document.createElement("div");
    const code = document.createElement("code"); code.textContent = l.id;
    id.append(document.createTextNode(l.keyHint + " "), document.createElement("br"), code);
    tr.append(cell(id), cell(l.customerEmail), cell(l.plan), cell(`${l.activeDevices} / ${l.maxDevices}`), cell(fmtDate(l.expiresAt)));
    const badge = document.createElement("span");
    badge.className = "badge " + l.status.split(" ")[0]; badge.textContent = l.status;
    tr.append(cell(badge));
    const open = document.createElement("button"); open.className = "link"; open.textContent = "Manage";
    open.onclick = () => openDetail(l);
    tr.append(cell(open));
    rows.append(tr);
  }
}

async function createLicenses() {
  const expiry = $("fixedExpiry").value;
  const body = {
    plan: $("plan").value,
    maxDevices: Number($("maxDevices").value),
    quantity: Number($("quantity").value),
    customerEmail: $("customerEmail").value.trim() || null,
    expiresAt: expiry ? new Date(expiry + "T23:59:59Z").toISOString() : null,
  };
  try {
    const created = await api("POST", "/licenses", body);
    $("createdKeysText").textContent = created.map((c) => c.licenseKey).join("\n");
    $("createdKeys").hidden = false;
    await loadLicenses();
  } catch (e) { toast(e.message); }
}

async function openDetail(l) {
  selected = l;
  $("detail").hidden = false;
  $("detailTitle").textContent = `${l.keyHint} · ${l.plan} · ${l.status}`;
  $("editExpiry").value = l.expiresAt ? l.expiresAt.substring(0, 10) : "";
  $("editMax").value = l.maxDevices;
  $("revokeBtn").hidden = !!l.revokedAt;
  $("reinstateBtn").hidden = !l.revokedAt;
  const acts = await api("GET", `/licenses/${encodeURIComponent(l.id)}/activations`);
  const rows = $("activationRows"); rows.replaceChildren();
  for (const a of acts) {
    const tr = document.createElement("tr");
    const dev = document.createElement("code"); dev.textContent = a.deviceId;
    tr.append(cell(a.deviceName), cell(dev), cell(a.appVersion), cell(fmt(a.activatedAt)), cell(fmt(a.lastSeenAt)), cell(a.deactivatedAt ? "Deactivated" : "Active"));
    if (!a.deactivatedAt) {
      const b = document.createElement("button"); b.className = "link"; b.textContent = "Deactivate";
      b.onclick = async () => { await api("POST", `/activations/${a.id}/deactivate`); toast("Device deactivated."); await loadLicenses(); openDetail(licenses.find((x) => x.id === l.id)); };
      tr.append(cell(b));
    } else tr.append(cell(""));
    rows.append(tr);
  }
  $("detail").scrollIntoView({ behavior: "smooth" });
}

async function saveLicense() {
  const exp = $("editExpiry").value;
  try {
    await api("PATCH", `/licenses/${encodeURIComponent(selected.id)}`, {
      expiresAt: exp ? new Date(exp + "T23:59:59Z").toISOString() : null,
      maxDevices: Number($("editMax").value),
    });
    toast("License updated."); await loadLicenses(); openDetail(licenses.find((x) => x.id === selected.id));
  } catch (e) { toast(e.message); }
}

async function licenseAction(path, body, message) {
  try {
    await api("POST", `/licenses/${encodeURIComponent(selected.id)}/${path}`, body);
    toast(message); await loadLicenses(); openDetail(licenses.find((x) => x.id === selected.id));
  } catch (e) { toast(e.message); }
}

async function loadCustomers() {
  const list = await api("GET", "/customers");
  const rows = $("customerRows"); rows.replaceChildren();
  for (const c of list) {
    const tr = document.createElement("tr");
    const id = document.createElement("code"); id.textContent = c.id;
    tr.append(cell(c.email), cell(c.name), cell(fmt(c.createdAt)), cell(id));
    rows.append(tr);
  }
}

async function loadAudit() {
  const list = await api("GET", "/audit?limit=300");
  const rows = $("auditRows"); rows.replaceChildren();
  for (const a of list) {
    const tr = document.createElement("tr");
    tr.append(cell(fmt(a.timestamp)), cell(a.actor), cell(a.action), cell(a.details));
    rows.append(tr);
  }
}

document.querySelectorAll(".nav").forEach((btn) => btn.addEventListener("click", async () => {
  document.querySelectorAll(".nav").forEach((b) => b.classList.toggle("active", b === btn));
  document.querySelectorAll(".tab").forEach((t) => (t.hidden = t.id !== "tab-" + btn.dataset.tab));
  try {
    if (btn.dataset.tab === "licenses") await loadLicenses();
    if (btn.dataset.tab === "customers") await loadCustomers();
    if (btn.dataset.tab === "audit") await loadAudit();
  } catch (e) { toast(e.message); }
}));

$("loginBtn").onclick = login;
$("adminKey").addEventListener("keydown", (e) => e.key === "Enter" && login());
$("logoutBtn").onclick = logout;
$("createBtn").onclick = createLicenses;
$("licenseFilter").oninput = renderLicenses;
$("closeDetail").onclick = () => ($("detail").hidden = true);
$("saveLicense").onclick = saveLicense;
$("clearExpiry").onclick = async () => {
  await api("PATCH", `/licenses/${encodeURIComponent(selected.id)}`, { clearExpiry: true });
  toast("Expiry removed."); await loadLicenses(); openDetail(licenses.find((x) => x.id === selected.id));
};
$("revokeBtn").onclick = () => {
  const reason = prompt("Reason for revocation (shown in the audit log):", "");
  if (reason !== null) licenseAction("revoke", { reason }, "License revoked.");
};
$("reinstateBtn").onclick = () => licenseAction("reinstate", null, "License reinstated.");
$("addCustomer").onclick = async () => {
  try {
    await api("POST", "/customers", { email: $("custEmail").value, name: $("custName").value || null, notes: $("custNotes").value || null });
    toast("Customer added."); await loadCustomers();
  } catch (e) { toast(e.message); }
};

if (adminKey) { $("login").hidden = true; $("app").hidden = false; loadLicenses().catch((e) => toast(e.message)); }
