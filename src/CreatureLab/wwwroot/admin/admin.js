// Moderator page: approve or reject the queue, remove creatures from the tank, change settings.
import { createHub, keepConnected } from "/shared/hub.js";

const HABITAT_LABELS = { swimmer: "Swimmer", bottomCrawler: "Bottom crawler", floater: "Floater" };
const PIN_KEY = "creatureLab.adminPin";
const SOUND_KEY = "creatureLab.adminSound";
const REMOVE_CONFIRM_MS = 3000;

const $ = (id) => document.getElementById(id);

// sessionStorage keeps the PIN across reloads of this tab only. It can throw in private mode.
const session = {
  get(key) {
    try {
      return sessionStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key, value) {
    try {
      if (value === null) sessionStorage.removeItem(key);
      else sessionStorage.setItem(key, value);
    } catch {
      // Only remembered in memory.
    }
  },
};

const state = {
  pin: session.get(PIN_KEY),
  pending: new Map(), // id -> creature summary
  tank: new Map(),
  settings: null,
  sound: session.get(SOUND_KEY) !== "off",
  hubStarted: false,
};

let audio = null;
const hub = createHub();

// ---------- Unlock ----------

$("pin-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  audio ??= createAudio(); // created inside a user gesture so it's allowed to play later
  const pin = $("pin").value.trim();
  if (!pin) return;
  state.pin = pin;
  if (await refreshAll()) {
    session.set(PIN_KEY, pin);
    showApp();
  }
});

$("lock").addEventListener("click", () => lock(""));

function lock(message) {
  state.pin = null;
  session.set(PIN_KEY, null);
  $("app").hidden = true;
  $("lock-view").hidden = false;
  $("pin").value = "";
  showPinError(message);
}

function showPinError(message) {
  $("pin-error").textContent = message;
  $("pin-error").hidden = !message;
}

function showApp() {
  showPinError("");
  $("lock-view").hidden = true;
  $("app").hidden = false;
  if (!state.hubStarted) {
    state.hubStarted = true;
    keepConnected(hub, onHubConnected, (s) => ($("conn").dataset.state = s));
  }
}

async function onHubConnected() {
  if (!state.pin) return;
  const ok = await hub.invoke("JoinAdmin", state.pin);
  if (!ok) {
    lock("Wrong PIN.");
    return;
  }
  await refreshAll(); // catch up on anything that happened while disconnected
}

// ---------- API ----------

async function api(path, { method = "GET", body } = {}) {
  const headers = { "X-Admin-Pin": state.pin ?? "" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  if (res.status === 401 || res.status === 429) {
    const err = await res.json().catch(() => ({}));
    lock(err.error || "Wrong PIN.");
    throw new Error("locked");
  }
  return res;
}

async function refreshAll() {
  try {
    const [pending, tank, settings, stats] = await Promise.all([
      api("/api/admin/pending").then((r) => r.json()),
      fetch("/api/tank").then((r) => r.json()),
      api("/api/admin/settings").then((r) => r.json()),
      api("/api/admin/stats").then((r) => r.json()),
    ]);
    state.pending = new Map(pending.map((c) => [c.id, c]));
    state.tank = new Map(tank.map((c) => [c.id, c]));
    renderPending();
    renderTank();
    applySettings(settings);
    renderStats(stats);
    return true;
  } catch (err) {
    if (err.message !== "locked") {
      console.error(err);
      showPinError("Couldn't reach the server. Is it running?");
    }
    return false;
  }
}

// ---------- Live events ----------

hub.on("CreatureSubmitted", (c) => {
  if (state.pending.has(c.id)) return;
  state.pending.set(c.id, c);
  renderPending();
  notifyNewDrawing();
});

hub.on("CreatureApproved", (c) => {
  state.pending.delete(c.id);
  state.tank.set(c.id, c);
  renderPending();
  renderTank();
});

hub.on("CreatureRejected", ({ id }) => {
  state.pending.delete(id);
  renderPending();
});

for (const event of ["CreatureRemoved", "CreatureRetired"]) {
  hub.on(event, ({ id }) => {
    state.tank.delete(id);
    renderTank();
  });
}

hub.on("SettingsChanged", applySettings);
hub.on("Stats", renderStats);

// ---------- Rendering ----------

function renderPending() {
  // Oldest first: they've been waiting longest.
  const items = [...state.pending.values()].sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  renderList($("pending-list"), items, (card, c) => {
    const approve = button("Approve", "btn approve", () => decide(c.id, "approve", card));
    const reject = button("Reject", "btn reject", () => decide(c.id, "reject", card));
    card.querySelector(".buttons").append(reject, approve);
  });
  $("pending-empty").hidden = items.length > 0;
  const count = $("pending-count");
  count.textContent = items.length;
  count.classList.toggle("has-items", items.length > 0);
  document.title = items.length > 0 ? `(${items.length}) Admin · Creature Lab` : "Admin · Deep Sea Creature Lab";
}

function renderTank() {
  // Newest first: the most likely to need removing.
  const items = [...state.tank.values()].sort((a, b) => (b.decidedAt ?? "").localeCompare(a.decidedAt ?? ""));
  renderList($("tank-list"), items, (card, c) => {
    const remove = button("Remove", "btn remove", () => {
      if (!remove.classList.contains("confirm")) {
        remove.classList.add("confirm");
        remove.textContent = "Tap again to remove";
        setTimeout(() => {
          remove.classList.remove("confirm");
          remove.textContent = "Remove";
        }, REMOVE_CONFIRM_MS);
        return;
      }
      decide(c.id, "remove", card);
    });
    card.querySelector(".buttons").append(remove);
  });
  $("tank-empty").hidden = items.length > 0;
  $("tank-count").textContent = items.length;
}

/** Rebuilds a list, reusing existing cards so in-progress taps and animations aren't lost. */
function renderList(container, items, addButtons) {
  const existing = new Map([...container.children].map((el) => [el.dataset.id, el]));
  const cards = items.map((c) => {
    const card = existing.get(c.id) ?? createCard(c, addButtons);
    card.querySelector("time").textContent = timeAgo(c.createdAt);
    return card;
  });
  container.replaceChildren(...cards);
}

function createCard(c, addButtons) {
  const card = $("card-template").content.firstElementChild.cloneNode(true);
  card.dataset.id = c.id;
  const img = card.querySelector("img");
  img.src = c.imageUrl;
  img.alt = c.creatureName;
  card.querySelector(".name").textContent = c.creatureName;
  card.querySelector(".by").textContent = `by ${c.artistDisplay}`;
  card.querySelector(".habitat").textContent = HABITAT_LABELS[c.habitat] ?? c.habitat;
  card.querySelector("time").dateTime = c.createdAt;
  addButtons(card, c);
  return card;
}

function button(label, className, onClick) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = className;
  b.textContent = label;
  b.addEventListener("click", onClick);
  return b;
}

async function decide(id, action, card) {
  for (const b of card.querySelectorAll("button")) b.disabled = true;
  try {
    const res = await api(`/api/admin/creatures/${id}/${action}`, { method: "POST" });
    if (res.ok) {
      const c = await res.json();
      state.pending.delete(id);
      if (action === "approve") state.tank.set(id, c);
      if (action === "remove") state.tank.delete(id);
    } else {
      // Someone else already decided (two moderators), or it's gone. Resync.
      await refreshAll();
    }
  } catch (err) {
    if (err.message === "locked") return;
    console.error(err);
    alert("That didn't go through. Check the connection and try again.");
  } finally {
    for (const b of card.querySelectorAll("button")) b.disabled = false;
    renderPending();
    renderTank();
  }
}

function renderStats(stats) {
  for (const dd of document.querySelectorAll("[data-stat]")) {
    const value = stats[dd.dataset.stat];
    if (value !== undefined) dd.textContent = value;
  }
}

function timeAgo(iso) {
  const seconds = Math.max(0, (Date.now() - Date.parse(iso)) / 1000);
  if (seconds < 45) return "just now";
  if (seconds < 90) return "1 min ago";
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  return new Date(iso).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
}

setInterval(() => {
  for (const el of document.querySelectorAll(".card")) {
    const c = state.pending.get(el.dataset.id) ?? state.tank.get(el.dataset.id);
    if (c) el.querySelector("time").textContent = timeAgo(c.createdAt);
  }
}, 15000);

// ---------- Tabs ----------

for (const tab of document.querySelectorAll(".tab")) {
  tab.addEventListener("click", () => {
    for (const t of document.querySelectorAll(".tab")) {
      const selected = t === tab;
      t.setAttribute("aria-selected", String(selected));
      $(`tab-${t.dataset.tab}`).hidden = !selected;
    }
  });
}

// ---------- Settings ----------

function applySettings(settings) {
  state.settings = settings;
  $("auto-approve").checked = settings.autoApprove;
  $("auto-banner").hidden = !settings.autoApprove;
  if (document.activeElement !== $("public-url")) $("public-url").value = settings.publicBaseUrl;
  if (document.activeElement !== $("snapshot-interval")) $("snapshot-interval").value = settings.snapshotIntervalMinutes;
}

async function saveSettings(update, noteId, successText) {
  const note = $(noteId);
  try {
    const res = await api("/api/admin/settings", { method: "POST", body: update });
    const body = await res.json();
    if (!res.ok) {
      setNote(note, body.error || "Couldn't save.", true);
      return false;
    }
    applySettings(body);
    setNote(note, successText, false);
    return true;
  } catch (err) {
    if (err.message !== "locked") setNote(note, "Couldn't reach the server.", true);
    return false;
  }
}

function setNote(note, text, bad) {
  if (!note) return;
  note.textContent = text;
  note.classList.toggle("bad", bad);
}

$("auto-approve").addEventListener("change", async (e) => {
  const wanted = e.target.checked;
  if (!(await saveSettings({ autoApprove: wanted }))) e.target.checked = !wanted;
});

$("auto-off").addEventListener("click", () => saveSettings({ autoApprove: false }));

$("url-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const url = $("public-url").value.trim();
  saveSettings({ publicBaseUrl: url }, "url-note", url ? "Saved. The QR code on the TV now points here." : "Cleared.");
});

$("snapshot-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const minutes = Number($("snapshot-interval").value);
  saveSettings({ snapshotIntervalMinutes: minutes }, "snapshot-note", minutes > 0 ? `Saved: every ${minutes} min.` : "Snapshots off.");
});

$("snapshot-now").addEventListener("click", async () => {
  try {
    await api("/api/admin/snapshot", { method: "POST" });
    setNote($("snapshot-note"), "Asked the TV to take a snapshot.", false);
  } catch (err) {
    if (err.message !== "locked") setNote($("snapshot-note"), "Couldn't reach the server.", true);
  }
});

$("download").addEventListener("click", async () => {
  const btn = $("download");
  const note = $("download-note");
  btn.disabled = true;
  setNote(note, "Building the zip…", false);
  try {
    // fetch (not a plain link) so the PIN can travel in a header instead of the URL.
    const res = await api("/api/admin/export");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const blob = await res.blob();
    const name = /filename="?([^";]+)"?/.exec(res.headers.get("Content-Disposition") ?? "")?.[1] ?? "creature-lab-gallery.zip";
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    document.body.append(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    setNote(note, `Downloaded ${name} (${(blob.size / 1024 / 1024).toFixed(1)} MB).`, false);
  } catch (err) {
    if (err.message !== "locked") setNote(note, "Download failed. Try again.", true);
  } finally {
    btn.disabled = false;
  }
});

$("sound").checked = state.sound;
$("sound").addEventListener("change", (e) => {
  state.sound = e.target.checked;
  session.set(SOUND_KEY, state.sound ? "on" : "off");
  audio ??= createAudio();
});

function notifyNewDrawing() {
  if (!state.sound) return;
  navigator.vibrate?.(120);
  if (!audio) return;
  try {
    audio.resume?.();
    const osc = audio.createOscillator();
    const gain = audio.createGain();
    osc.type = "sine";
    osc.frequency.setValueAtTime(880, audio.currentTime);
    osc.frequency.exponentialRampToValueAtTime(1320, audio.currentTime + 0.12);
    gain.gain.setValueAtTime(0.0001, audio.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.25, audio.currentTime + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, audio.currentTime + 0.25);
    osc.connect(gain).connect(audio.destination);
    osc.start();
    osc.stop(audio.currentTime + 0.3);
  } catch {
    // Audio blocked; the badge and title still update.
  }
}

function createAudio() {
  try {
    return new (window.AudioContext || window.webkitAudioContext)();
  } catch {
    return null;
  }
}

// ---------- Start ----------

if (state.pin) {
  refreshAll().then((ok) => ok && showApp());
}
