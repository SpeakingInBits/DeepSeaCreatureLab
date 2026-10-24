// Visitor page: pick a habitat, draw, name, sign, release, then watch the status.
import { createHub, keepConnected } from "/shared/hub.js";
import { MYSTERY_DIVER } from "/shared/theme.js";
import { renderKeepsakeCard, shortUrl } from "/draw/keepsake.js";
/* global signalR */

const SIZE = 512; // internal canvas resolution
const COLORS = ["#ff4d4d", "#ff9f1c", "#ffe14d", "#7dff6b", "#3de8ff", "#4d7dff", "#b46bff", "#ff6bd6", "#111111"];
const COLOR_NAMES = ["Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple", "Pink", "Black"];
const BRUSHES = { thin: 9, thick: 24 };
const MAX_UPLOAD_BYTES = 250 * 1024;
const CROP_PADDING = 12;
const POLL_DISCONNECTED_MS = 5000;
const POLL_CONNECTED_MS = 15000;
const KEYS = { clientId: "creatureLab.clientId", artist: "creatureLab.artistName", creature: "creatureLab.creatureName" };
const GUIDE_HINTS = { swimmer: "head on the right →", bottomCrawler: "feet on the dashed line", floater: "it drifts near the top" };
const STATUS_TEXT = {
  pending: ["Waiting for approval…", "A crew member checks every creature before it goes on the big screen."],
  approved: ["Your creature is in the tank!", "Look at the TV!"],
  rejected: ["This one didn't make it into the tank", "The crew keeps the tank fun for everyone. Want to draw another?"],
};

const $ = (id) => document.getElementById(id);
const els = {
  drawView: $("draw-view"),
  statusView: $("status-view"),
  guide: $("guide"),
  paint: $("paint"),
  guideHint: $("guide-hint"),
  palette: $("palette"),
  undo: $("undo"),
  clear: $("clear"),
  creatureName: $("creature-name"),
  artistName: $("artist-name"),
  creatureCount: $("creature-count"),
  artistCount: $("artist-count"),
  release: $("release"),
  releaseHint: $("release-hint"),
  error: $("form-error"),
  statusBox: $("status-box"),
  statusTitle: $("status-title"),
  statusText: $("status-text"),
  preview: $("preview"),
  save: $("save"),
  drawAgain: $("draw-again"),
  programLinks: $("program-links"),
  saveOverlay: $("save-overlay"),
  saveImage: $("save-image"),
  saveDownload: $("save-download"),
  saveClose: $("save-close"),
};

// localStorage can throw (private mode, blocked storage); the page must work without it.
const storage = {
  get(key) {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Not remembered on this device; that's fine.
    }
  },
};

const clientId = loadClientId();
const paintCtx = els.paint.getContext("2d");
const guideCtx = els.guide.getContext("2d");

const state = {
  habitat: "swimmer",
  color: COLORS[4],
  brush: "thick",
  strokes: [], // { color, size, points: [[x, y], ...] } or { clear: true } (so Clear can be undone)
  current: null,
  pointerId: null,
  config: { rateLimitSeconds: 30, maxNameLength: 20, programs: [] },
  cooldownUntil: 0,
  submitting: false,
  submission: null, // { id, status, name }
  card: null, // { canvas, file, url, name }
  lastPollAt: 0,
};

// ---------- Setup ----------

buildPalette();
drawGuide();
els.artistName.value = storage.get(KEYS.artist) ?? "";
updateCounters();
updateControls();
loadConfig();

document.addEventListener("gesturestart", (e) => e.preventDefault()); // iOS pinch-zoom

for (const button of document.querySelectorAll(".habitat")) {
  button.addEventListener("click", () => {
    state.habitat = button.dataset.habitat;
    for (const b of document.querySelectorAll(".habitat")) b.setAttribute("aria-checked", String(b === button));
    els.guideHint.textContent = GUIDE_HINTS[state.habitat];
    drawGuide();
  });
}

for (const button of document.querySelectorAll(".brush")) {
  button.addEventListener("click", () => {
    state.brush = button.dataset.brush;
    for (const b of document.querySelectorAll(".brush")) b.setAttribute("aria-checked", String(b === button));
  });
}

els.undo.addEventListener("click", () => {
  state.strokes.pop();
  redraw();
  updateControls();
});

els.clear.addEventListener("click", () => {
  if (!hasDrawing()) return;
  state.strokes.push({ clear: true });
  redraw();
  updateControls();
});

for (const [input, key] of [[els.creatureName, KEYS.creature], [els.artistName, KEYS.artist]]) {
  input.addEventListener("input", () => {
    storage.set(key, input.value);
    updateCounters();
    updateControls();
    showError("");
  });
}

els.creatureName.addEventListener("keydown", (e) => {
  if (e.key === "Enter") els.artistName.focus();
});
els.artistName.addEventListener("keydown", (e) => {
  if (e.key === "Enter") els.artistName.blur();
});

els.release.addEventListener("click", release);
els.save.addEventListener("click", saveToPhone);
els.saveClose.addEventListener("click", () => (els.saveOverlay.hidden = true));
els.drawAgain.addEventListener("click", drawAgain);

// ---------- Drawing ----------

els.paint.addEventListener("contextmenu", (e) => e.preventDefault());

els.paint.addEventListener("pointerdown", (e) => {
  if (state.pointerId !== null) return; // ignore a second finger
  e.preventDefault();
  state.pointerId = e.pointerId;
  els.paint.setPointerCapture(e.pointerId);
  const point = toCanvasPoint(e);
  state.current = { color: state.color, size: BRUSHES[state.brush], points: [point] };
  state.strokes.push(state.current);
  drawDot(paintCtx, state.current, point);
  updateControls();
});

els.paint.addEventListener("pointermove", (e) => {
  if (e.pointerId !== state.pointerId || !state.current) return;
  e.preventDefault();
  const events = e.getCoalescedEvents?.() ?? [];
  for (const ev of events.length ? events : [e]) {
    const point = toCanvasPoint(ev);
    const points = state.current.points;
    const last = points[points.length - 1];
    if (Math.hypot(point[0] - last[0], point[1] - last[1]) < 1.5) continue;
    points.push(point);
    drawSegment(paintCtx, state.current, last, point);
  }
});

for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
  els.paint.addEventListener(type, (e) => {
    if (e.pointerId !== state.pointerId) return;
    state.pointerId = null;
    state.current = null;
    updateControls();
  });
}

function toCanvasPoint(e) {
  const rect = els.paint.getBoundingClientRect();
  return [((e.clientX - rect.left) * SIZE) / rect.width, ((e.clientY - rect.top) * SIZE) / rect.height];
}

function applyBrush(ctx, stroke) {
  ctx.strokeStyle = stroke.color;
  ctx.fillStyle = stroke.color;
  ctx.lineWidth = stroke.size;
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
}

function drawDot(ctx, stroke, [x, y]) {
  applyBrush(ctx, stroke);
  ctx.beginPath();
  ctx.arc(x, y, stroke.size / 2, 0, Math.PI * 2);
  ctx.fill();
}

function drawSegment(ctx, stroke, [ax, ay], [bx, by]) {
  applyBrush(ctx, stroke);
  ctx.beginPath();
  ctx.moveTo(ax, ay);
  ctx.lineTo(bx, by);
  ctx.stroke();
}

function drawStroke(ctx, stroke) {
  const [first, ...rest] = stroke.points;
  if (rest.length === 0) return drawDot(ctx, stroke, first);
  applyBrush(ctx, stroke);
  ctx.beginPath();
  ctx.moveTo(first[0], first[1]);
  for (const [x, y] of rest) ctx.lineTo(x, y);
  ctx.stroke();
}

function visibleStrokes() {
  const lastClear = state.strokes.findLastIndex((s) => s.clear);
  return state.strokes.slice(lastClear + 1);
}

function hasDrawing() {
  return visibleStrokes().length > 0;
}

function redraw() {
  paintCtx.clearRect(0, 0, SIZE, SIZE);
  for (const stroke of visibleStrokes()) drawStroke(paintCtx, stroke);
}

function buildPalette() {
  COLORS.forEach((color, i) => {
    const swatch = document.createElement("button");
    swatch.type = "button";
    swatch.className = "swatch";
    swatch.setAttribute("role", "radio");
    swatch.setAttribute("aria-label", COLOR_NAMES[i]);
    swatch.setAttribute("aria-checked", String(color === state.color));
    swatch.style.setProperty("--swatch", color);
    swatch.addEventListener("click", () => {
      state.color = color;
      for (const s of els.palette.children) s.setAttribute("aria-checked", String(s === swatch));
    });
    els.palette.append(swatch);
  });
}

/** A faint dashed outline for the chosen habitat. It is on its own canvas and never uploaded. */
function drawGuide() {
  const g = guideCtx;
  g.clearRect(0, 0, SIZE, SIZE);
  g.save();
  g.strokeStyle = "rgba(220, 240, 255, 0.3)";
  g.fillStyle = "rgba(220, 240, 255, 0.3)";
  g.lineWidth = 4;
  g.lineCap = "round";
  g.lineJoin = "round";
  g.setLineDash([14, 12]);

  if (state.habitat === "swimmer") {
    // Fish facing right: tail on the left, head on the right.
    g.beginPath();
    g.ellipse(285, 256, 150, 95, 0, 0, Math.PI * 2);
    g.stroke();
    g.beginPath();
    g.moveTo(140, 256);
    g.lineTo(52, 172);
    g.quadraticCurveTo(80, 256, 52, 340);
    g.closePath();
    g.stroke();
    g.beginPath();
    g.moveTo(250, 165);
    g.quadraticCurveTo(290, 110, 330, 168);
    g.stroke();
    g.setLineDash([]);
    g.beginPath();
    g.arc(375, 232, 11, 0, Math.PI * 2);
    g.fill();
  } else if (state.habitat === "bottomCrawler") {
    // Crab-like body standing on the sand line.
    g.beginPath();
    g.ellipse(256, 345, 140, 80, 0, Math.PI, Math.PI * 2);
    g.closePath();
    g.stroke();
    for (const [x1, x2] of [[150, 112], [190, 170], [322, 342], [362, 400]]) {
      g.beginPath();
      g.moveTo(x1, 345);
      g.quadraticCurveTo((x1 + x2) / 2, 385, x2, 432);
      g.stroke();
    }
    for (const x of [300, 350]) {
      g.beginPath();
      g.moveTo(x - 10, 275);
      g.lineTo(x, 220);
      g.stroke();
      g.beginPath();
      g.arc(x, 210, 11, 0, Math.PI * 2);
      g.stroke();
    }
    g.strokeStyle = "rgba(255, 209, 102, 0.45)";
    g.beginPath();
    g.moveTo(24, 440);
    g.lineTo(488, 440);
    g.stroke();
  } else {
    // Jellyfish: dome on top, tentacles trailing down.
    g.beginPath();
    g.ellipse(256, 210, 140, 115, 0, Math.PI, Math.PI * 2);
    for (let x = 396; x > 116; x -= 35) g.quadraticCurveTo(x - 17, 238, x - 35, 210);
    g.stroke();
    for (const x of [160, 210, 256, 302, 352]) {
      g.beginPath();
      g.moveTo(x, 225);
      g.bezierCurveTo(x - 25, 300, x + 25, 360, x, 450);
      g.stroke();
    }
  }
  g.restore();
}

// ---------- Names and controls ----------

function cleanName(input) {
  return input.value.trim().replace(/\s+/g, " ");
}

function updateCounters() {
  const max = state.config.maxNameLength;
  els.creatureCount.textContent = `${els.creatureName.value.length}/${max}`;
  els.artistCount.textContent = `${els.artistName.value.length}/${max}`;
}

function cooldownSeconds() {
  return Math.max(0, Math.ceil((state.cooldownUntil - Date.now()) / 1000));
}

function updateControls() {
  const drawn = hasDrawing();
  const named = cleanName(els.creatureName).length > 0;
  const wait = cooldownSeconds();

  els.release.disabled = state.submitting || !drawn || !named || wait > 0;
  els.release.textContent = state.submitting
    ? "Releasing…"
    : wait > 0
      ? `Release again in ${wait}s`
      : "Release into the tank";
  els.releaseHint.textContent = !drawn ? "Draw your creature first." : !named ? "Give it a name to release it." : "";
  els.undo.disabled = state.strokes.length === 0;
  els.clear.disabled = !drawn;
}

function startCooldown(seconds) {
  state.cooldownUntil = Date.now() + seconds * 1000;
  const tick = () => {
    updateControls();
    if (cooldownSeconds() > 0) setTimeout(tick, 500);
  };
  tick();
}

function showError(message) {
  els.error.textContent = message;
  els.error.hidden = !message;
}

async function loadConfig() {
  try {
    const res = await fetch("/api/config");
    if (!res.ok) return;
    state.config = await res.json();
    for (const input of [els.creatureName, els.artistName]) input.maxLength = state.config.maxNameLength;
    updateCounters();
    renderProgramLinks();
  } catch (err) {
    console.warn("Could not load config", err);
  }
}

function renderProgramLinks() {
  els.programLinks.replaceChildren();
  for (const program of state.config.programs) {
    if (!/^https?:\/\//i.test(program.url)) continue;
    const a = document.createElement("a");
    a.className = "program";
    a.href = program.url;
    a.target = "_blank";
    a.rel = "noopener";
    a.textContent = program.label;
    const small = document.createElement("small");
    small.textContent = shortUrl(program.url);
    a.append(small);
    els.programLinks.append(a);
  }
}

// ---------- Release ----------

async function release() {
  if (els.release.disabled) return;
  state.submitting = true;
  updateControls();
  showError("");

  const creatureName = cleanName(els.creatureName);
  const artistName = cleanName(els.artistName);

  try {
    const drawing = await exportDrawing();
    if (!drawing) {
      showError("Draw something first!");
      return;
    }

    // Render the keepsake now, so "Save to my phone" can share it synchronously in its tap.
    prepareCard(drawing.canvas, creatureName, artistName || MYSTERY_DIVER);

    const form = new FormData();
    form.append("image", drawing.blob, "creature.png");
    form.append("creatureName", creatureName);
    form.append("artistName", artistName);
    form.append("habitat", state.habitat);
    form.append("clientId", clientId);

    const res = await fetch("/api/creatures", { method: "POST", body: form });
    const body = await res.json().catch(() => ({}));
    if (res.ok) {
      startCooldown(state.config.rateLimitSeconds);
      showStatus({ id: body.id, status: body.status, name: creatureName });
      return;
    }
    if (res.status === 429 && body.retryAfterSeconds) startCooldown(body.retryAfterSeconds);
    showError(body.error || "Something went wrong. Try again?");
  } catch (err) {
    console.error(err);
    showError("We couldn't reach the tank. Check your connection and try again.");
  } finally {
    state.submitting = false;
    updateControls();
  }
}

/** Crops to the drawing plus padding and exports a PNG of at most 512 px and 250 KB. */
async function exportDrawing() {
  const { data } = paintCtx.getImageData(0, 0, SIZE, SIZE);
  let minX = SIZE, minY = SIZE, maxX = -1, maxY = -1;
  for (let y = 0; y < SIZE; y++) {
    const row = y * SIZE * 4;
    for (let x = 0; x < SIZE; x++) {
      if (data[row + x * 4 + 3] > 8) {
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
      }
    }
  }
  if (maxX < 0) return null;

  minX = Math.max(0, minX - CROP_PADDING);
  minY = Math.max(0, minY - CROP_PADDING);
  maxX = Math.min(SIZE - 1, maxX + CROP_PADDING);
  maxY = Math.min(SIZE - 1, maxY + CROP_PADDING);
  const w = maxX - minX + 1;
  const h = maxY - minY + 1;

  let scale = Math.min(1, SIZE / Math.max(w, h));
  for (let attempt = 0; attempt < 6; attempt++) {
    const out = document.createElement("canvas");
    out.width = Math.max(1, Math.round(w * scale));
    out.height = Math.max(1, Math.round(h * scale));
    out.getContext("2d").drawImage(els.paint, minX, minY, w, h, 0, 0, out.width, out.height);
    const blob = await new Promise((resolve) => out.toBlob(resolve, "image/png"));
    if (blob && blob.size <= MAX_UPLOAD_BYTES) return { blob, canvas: out };
    scale *= 0.8;
  }
  throw new Error("Drawing too large to upload");
}

// ---------- Status ----------

function showStatus(submission) {
  state.submission = submission;
  els.drawView.hidden = true;
  els.statusView.hidden = false;
  window.scrollTo(0, 0);
  renderStatus();
  schedulePoll();
}

function renderStatus() {
  const raw = state.submission.status;
  // A later "retired" or "removed" still reads best as approved / not this time.
  const status = raw === "retired" ? "approved" : raw === "removed" ? "rejected" : raw;
  const [title, text] = STATUS_TEXT[status] ?? STATUS_TEXT.pending;
  els.statusBox.dataset.status = status;
  els.statusTitle.textContent = title;
  els.statusText.textContent = text;
  els.drawAgain.classList.toggle("primary", status === "rejected");
  els.drawAgain.classList.toggle("secondary", status !== "rejected");
}

function updateSubmission(id, status) {
  const sub = state.submission;
  if (!sub || sub.id !== id || sub.status === status) return;
  sub.status = status;
  renderStatus();
  if (status === "approved") navigator.vibrate?.([80, 60, 80]);
}

let pollTimer = null;

function schedulePoll() {
  clearTimeout(pollTimer);
  if (state.submission?.status !== "pending") return;
  pollTimer = setTimeout(async () => {
    const connected = hub.state === signalR.HubConnectionState.Connected;
    const interval = connected ? POLL_CONNECTED_MS : POLL_DISCONNECTED_MS;
    if (Date.now() - state.lastPollAt >= interval - 100) await pollStatus();
    schedulePoll();
  }, POLL_DISCONNECTED_MS);
}

async function pollStatus() {
  const sub = state.submission;
  if (!sub || sub.status !== "pending") return;
  state.lastPollAt = Date.now();
  try {
    const res = await fetch(`/api/creatures/${encodeURIComponent(sub.id)}`);
    if (res.ok) {
      const body = await res.json();
      updateSubmission(body.id, body.status);
    }
  } catch {
    // Offline for a moment; the next poll will try again.
  }
}

function drawAgain() {
  clearTimeout(pollTimer);
  state.submission = null;
  state.strokes = [];
  redraw();
  els.creatureName.value = "";
  storage.set(KEYS.creature, "");
  updateCounters();
  updateControls();
  showError("");
  els.statusView.hidden = true;
  els.drawView.hidden = false;
  window.scrollTo(0, 0);
}

// ---------- Save to my phone ----------

function prepareCard(drawingCanvas, creatureName, artistDisplay) {
  if (state.card?.url) URL.revokeObjectURL(state.card.url);

  const canvas = renderKeepsakeCard({
    drawing: drawingCanvas,
    creatureName,
    artistDisplay,
    programs: state.config.programs,
  });
  const name = `${creatureName.replace(/[^\p{L}\p{N}]+/gu, "-").replace(/^-|-$/g, "") || "my-creature"}.png`;
  const card = { canvas, file: null, url: null, name };
  state.card = card;
  els.preview.removeAttribute("src");

  canvas.toBlob((blob) => {
    if (!blob || state.card !== card) return;
    card.file = new File([blob], name, { type: "image/png" });
    card.url = URL.createObjectURL(blob);
    els.preview.src = card.url;
  }, "image/png");
}

function saveToPhone() {
  const card = state.card;
  if (!card) return;

  // Must run synchronously inside the tap: some browsers refuse share() after an await.
  if (card.file && navigator.canShare?.({ files: [card.file] })) {
    navigator.share({ files: [card.file] }).catch((err) => {
      if (err?.name !== "AbortError") showSaveOverlay();
    });
    return;
  }
  showSaveOverlay();
}

function showSaveOverlay() {
  const card = state.card;
  const src = card.url || card.canvas.toDataURL("image/png");
  els.saveImage.src = src;
  els.saveDownload.href = src;
  els.saveDownload.download = card.name;
  els.saveOverlay.hidden = false;
}

// ---------- Live connection ----------

const hub = createHub();
hub.on("CreatureApproved", (c) => updateSubmission(c.id, "approved"));
hub.on("CreatureRejected", (c) => updateSubmission(c.id, "rejected"));
keepConnected(hub, async () => {
  await hub.invoke("JoinVisitor", clientId);
  await pollStatus(); // catch up on anything decided while we were disconnected
});

function loadClientId() {
  let id = storage.get(KEYS.clientId);
  if (!id || !/^[A-Za-z0-9-]{8,64}$/.test(id)) {
    id = uuid();
    storage.set(KEYS.clientId, id);
  }
  return id;
}

function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID(); // only in secure contexts (https, localhost)
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}
