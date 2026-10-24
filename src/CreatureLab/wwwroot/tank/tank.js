// The TV: an animated deep-sea tank, the QR code, and the live "How it works" panel.
// Everything is drawn on one canvas, so a snapshot is simply canvas.toBlob().
import { theme } from "/shared/theme.js";
import { createHub, keepConnected } from "/shared/hub.js";
import { drawQr } from "/shared/qr.js";
import { paintWater, paintRays, paintBubble } from "/shared/scene.js";
import { seededRandom, roundRect, ellipsize, wrapLines, makeGlow } from "/shared/canvas.js";

// Sizes below are in layout units: 1 unit = 1 px on a 1080-pixel-tall screen.
const TAG_FULL_MS = 5000;
const TAG_SHRINK_MS = 700;
const MAX_BUBBLES = 260;
const TICKER_SIZE = 5;
const HABITATS = ["floater", "swimmer", "bottomCrawler"]; // also the drawing order, back to front
const SIZE_BY_HABITAT = { swimmer: 175, bottomCrawler: 145, floater: 160 };
const GLOW_BY_HABITAT = {
  swimmer: "rgba(110, 225, 255, 0.9)",
  bottomCrawler: "rgba(255, 200, 120, 0.85)",
  floater: "rgba(255, 130, 220, 0.9)",
};

const canvas = document.getElementById("tank");
const ctx = canvas.getContext("2d");

const state = {
  config: { publicBaseUrl: "", snapshotIntervalMinutes: 0, snapshotIncludeOverlay: true, populationCap: 30, creditLine: "", programs: [] },
  creatures: new Map(),
  loading: new Set(),
  cancelled: new Set(),
  arrivals: [], // newest first: { id, name, artist }
  stats: { connectedPhones: 0, submissionsToday: 0, messagesPerMinute: 0 },
  statChangedAt: {},
  dots: [], // messages travelling along the How-it-works diagram
  pulses: { phone: 0, server: 0, screen: 0 },
  bubbles: [],
  connection: "connecting",
  lastSnapshotAt: -Infinity,
  debug: false,
  fps: 0,
  frameMs: 0, // time spent updating and drawing, smoothed
};

// For the soak test: inspect from the browser console (creatureLab.state).
window.creatureLab = { state };

let W = 0, H = 0, u = 1, dpr = 1;
let L = null; // layout
let staticLayer = null; // water, distant ridges and seafloor, pre-rendered
let scenery = null; // kelp, marine snow, plankton, distant fish, vents
let qrCache = { key: null, canvas: null };
const cyanDot = makeGlow("rgba(70, 230, 255, 1)", 24);
const coralDot = makeGlow("rgba(255, 111, 181, 1)", 24);
const planktonGlow = makeGlow("rgba(120, 255, 220, 0.9)", 24);

const rand = (min, max) => min + Math.random() * (max - min);
const clamp = (v, min, max) => Math.min(max, Math.max(min, v));

// ---------- Layout and scenery ----------

function resize() {
  dpr = Math.min(window.devicePixelRatio || 1, 2);
  W = window.innerWidth;
  H = window.innerHeight;
  canvas.width = Math.round(W * dpr);
  canvas.height = Math.round(H * dpr);
  u = H / 1080;

  // The panel keeps a fixed size on the right; on ultrawide screens the tank just gets wider.
  const panelW = Math.round(Math.min(440 * u, W * 0.42));
  const headerH = 150 * u;
  const floorY = H - 120 * u;
  L = {
    panelX: W - panelW,
    panelW,
    tankRight: W - panelW,
    headerH,
    floorY,
    swimTop: headerH + 20 * u,
    swimBottom: floorY - 30 * u,
    floatTop: headerH,
    floatBottom: headerH + (floorY - headerH) * 0.38,
    walkY: floorY + 40 * u,
  };

  staticLayer = buildStaticLayer();
  scenery = buildScenery();
  qrCache = { key: null, canvas: null };
  for (const c of state.creatures.values()) {
    c.buildSprite();
    c.keepInside();
  }
}

function buildStaticLayer() {
  const c = document.createElement("canvas");
  c.width = canvas.width;
  c.height = canvas.height;
  const g = c.getContext("2d");
  g.scale(dpr, dpr);
  const random = seededRandom(7);

  paintWater(g, 0, 0, W, H);
  paintRidge(g, random, L.floorY - 120 * u, 90 * u, "rgba(5, 28, 52, 0.55)");
  paintRidge(g, random, L.floorY - 40 * u, 60 * u, "rgba(3, 18, 36, 0.8)");

  const sandTop = (x) => L.floorY + Math.sin(x / (180 * u)) * 8 * u + Math.sin(x / (63 * u) + 1.3) * 4 * u;
  g.beginPath();
  g.moveTo(0, H);
  for (let x = 0; x <= W + 8 * u; x += 8 * u) g.lineTo(x, sandTop(x));
  g.lineTo(W, H);
  g.closePath();
  const sand = g.createLinearGradient(0, L.floorY - 10 * u, 0, H);
  sand.addColorStop(0, "#28485e");
  sand.addColorStop(0.25, "#172f43");
  sand.addColorStop(1, "#08131f");
  g.fillStyle = sand;
  g.fill();

  g.strokeStyle = "rgba(140, 200, 230, 0.2)";
  g.lineWidth = 2 * u;
  g.beginPath();
  for (let x = 0; x <= W; x += 8 * u) (x === 0 ? g.moveTo : g.lineTo).call(g, x, sandTop(x));
  g.stroke();

  for (let i = 0; i < (500 * W) / 1920; i++) {
    g.fillStyle = `rgba(180, 210, 230, ${(0.04 + random() * 0.1).toFixed(3)})`;
    g.fillRect(random() * W, L.floorY + 8 * u + random() * (H - L.floorY), 2 * u, 2 * u);
  }

  for (let i = 0; i < 8; i++) {
    const x = random() * L.tankRight;
    const rw = (40 + random() * 100) * u;
    const rh = rw * (0.45 + random() * 0.3);
    const y = sandTop(x) + rh * 0.3;
    g.fillStyle = "#0c1d2c";
    g.beginPath();
    g.ellipse(x, y, rw / 2, rh / 2, 0, Math.PI, 0);
    g.fill();
    g.strokeStyle = "rgba(140, 200, 230, 0.12)";
    g.beginPath();
    g.ellipse(x, y, rw / 2, rh / 2, 0, Math.PI * 1.15, Math.PI * 1.6);
    g.stroke();
  }

  // Hydrothermal vents that bubbles rise from.
  for (const x of ventPositions()) {
    const y = sandTop(x);
    g.fillStyle = "#0a1824";
    g.beginPath();
    g.moveTo(x - 26 * u, y + 6 * u);
    g.lineTo(x - 9 * u, y - 46 * u);
    g.lineTo(x + 9 * u, y - 46 * u);
    g.lineTo(x + 26 * u, y + 6 * u);
    g.closePath();
    g.fill();
    const glow = g.createRadialGradient(x, y - 46 * u, 0, x, y - 46 * u, 22 * u);
    glow.addColorStop(0, "rgba(255, 170, 90, 0.45)");
    glow.addColorStop(1, "rgba(255, 170, 90, 0)");
    g.fillStyle = glow;
    g.fillRect(x - 22 * u, y - 68 * u, 44 * u, 44 * u);
  }

  return c;
}

function paintRidge(g, random, baseY, amp, color) {
  const p1 = random() * 10;
  const p2 = random() * 10;
  g.beginPath();
  g.moveTo(0, H);
  for (let x = 0; x <= W + 10 * u; x += 10 * u) {
    const n = Math.sin(x / (240 * u) + p1) * 0.6 + Math.sin(x / (97 * u) + p2) * 0.4;
    g.lineTo(x, baseY - (n + 1) * amp * 0.5);
  }
  g.lineTo(W, H);
  g.closePath();
  g.fillStyle = color;
  g.fill();
}

function ventPositions() {
  return [L.tankRight * 0.17, L.tankRight * 0.72];
}

function buildScenery() {
  const random = seededRandom(42);
  const tankW = L.tankRight;
  const kelpCount = Math.max(4, Math.round(tankW / (230 * u)));
  return {
    kelp: Array.from({ length: kelpCount }, (_, i) => ({
      x: ((i + 0.2 + random() * 0.6) / kelpCount) * tankW,
      h: (120 + random() * 280) * u,
      w: (10 + random() * 12) * u,
      phase: random() * Math.PI * 2,
      color: random() < 0.5 ? "#0e4f4a" : "#12573f",
    })),
    snow: Array.from({ length: Math.round((150 * W) / 1920) }, () => ({
      x: random() * W,
      y: random() * H,
      r: (0.7 + random() * 1.8) * u,
      vy: (6 + random() * 14) * u,
      phase: random() * Math.PI * 2,
      alpha: 0.2 + random() * 0.45,
    })),
    plankton: Array.from({ length: 24 }, () => ({
      x: random() * tankW,
      y: L.headerH + random() * (L.floorY - L.headerH),
      vx: (random() - 0.5) * 8 * u,
      phase: random() * Math.PI * 2,
      speed: 0.4 + random() * 0.9,
      size: (10 + random() * 16) * u,
    })),
    school: {
      x: random() * tankW,
      y: H * 0.45,
      dir: -1,
      speed: 16 * u,
      fish: Array.from({ length: 16 }, () => ({ dx: random() * 220 * u, dy: (random() - 0.5) * 80 * u, phase: random() * 6.28 })),
    },
    vents: ventPositions().map((x) => ({ x, next: 0 })),
  };
}

// ---------- Creatures ----------

class Creature {
  constructor(data, img, arrive) {
    this.id = data.id;
    this.name = data.creatureName;
    this.artist = data.artistDisplay;
    this.habitat = HABITATS.includes(data.habitat) ? data.habitat : "swimmer";
    this.approvedAt = Date.parse(data.decidedAt ?? data.createdAt) || Date.now();
    this.img = img;
    this.size = SIZE_BY_HABITAT[this.habitat] * rand(0.85, 1.15);
    this.phase = rand(0, Math.PI * 2);
    this.dir = Math.random() < 0.5 ? -1 : 1;
    this.facing = this.dir;
    this.alpha = 1;
    this.sx = 1;
    this.sy = 1;
    this.state = "living"; // arriving | living | leaving | popping | gone
    this.tagFullUntil = 0;
    this.nextTurn = 0;
    this.nextDepth = 0;
    this.nextPause = 0;
    this.pauseUntil = 0;
    this.popT = 0;

    const personality = {
      swimmer: { speed: rand(45, 100), wave: 0.075, waveSpeed: rand(5.5, 8) },
      bottomCrawler: { speed: rand(18, 40), wave: 0.012, waveSpeed: 3 },
      floater: { speed: rand(8, 20), wave: 0.05, waveSpeed: rand(1.4, 2) },
    }[this.habitat];
    this.speed = personality.speed;
    this.waveAmp = personality.wave;
    this.waveSpeed = personality.waveSpeed;
    this.bobAmp = rand(10, 24);
    this.bobSpeed = rand(0.6, 1.1);
    this.pulseSpeed = rand(1.2, 1.8);
    this.depth = rand(0, 30); // crawlers: how far forward on the sand

    this.buildSprite();
    if (arrive) this.enterFromSide();
    else this.placeRandomly();
  }

  /** Pre-scales the drawing to its on-screen size with a habitat-colored glow baked in. */
  buildSprite() {
    const k = (this.size * u) / Math.max(this.img.naturalWidth, this.img.naturalHeight);
    this.w = this.img.naturalWidth * k;
    this.h = this.img.naturalHeight * k;
    this.pad = Math.ceil(16 * u);
    this.sw = this.w + this.pad * 2;
    this.sh = this.h + this.pad * 2;

    const c = this.sprite ?? document.createElement("canvas");
    c.width = Math.ceil(this.sw * dpr);
    c.height = Math.ceil(this.sh * dpr);
    const g = c.getContext("2d");
    const p = this.pad * dpr, w = this.w * dpr, h = this.h * dpr;
    // Draw the image far off-canvas so only its shadow (the glow) lands here, then the image on top.
    // The glow also keeps black outlines readable against the dark water.
    g.shadowColor = GLOW_BY_HABITAT[this.habitat];
    g.shadowBlur = 12 * u * dpr;
    g.shadowOffsetX = 10000;
    g.drawImage(this.img, p - 10000, p, w, h);
    g.drawImage(this.img, p - 10000, p, w, h);
    g.shadowColor = "transparent";
    g.shadowBlur = 0;
    g.shadowOffsetX = 0;
    g.drawImage(this.img, p, p, w, h);
    this.sprite = c;
  }

  get minX() {
    return this.sw * 0.4 + 10 * u;
  }

  get maxX() {
    return Math.max(this.minX, L.tankRight - this.sw * 0.4 - 10 * u);
  }

  homeY() {
    if (this.habitat === "bottomCrawler") return L.walkY + this.depth * u - this.h / 2;
    if (this.habitat === "floater") return rand(L.floatTop + this.h / 2, Math.max(L.floatTop + this.h / 2, L.floatBottom));
    return rand(L.swimTop + this.h / 2, Math.max(L.swimTop + this.h / 2, L.swimBottom - this.h / 2));
  }

  placeRandomly() {
    this.x = rand(this.minX, this.maxX);
    this.y = this.baseY = this.targetY = this.homeY();
  }

  enterFromSide() {
    this.state = "arriving";
    const fromLeft = Math.random() < 0.6;
    this.dir = this.facing = fromLeft ? 1 : -1;
    this.x = fromLeft ? -this.sw / 2 : L.tankRight + this.sw / 2;
    this.y = this.baseY = this.targetY = this.homeY();
  }

  keepInside() {
    if (this.state === "living") this.x = clamp(this.x, this.minX, this.maxX);
    this.baseY = this.targetY = this.homeY();
  }

  leave() {
    if (this.state === "leaving" || this.state === "popping" || this.state === "gone") return;
    this.state = "leaving";
    this.dir = this.x < L.tankRight / 2 ? -1 : 1;
  }

  pop() {
    if (this.state === "popping" || this.state === "gone") return;
    this.state = "popping";
    this.popT = 0;
    burst(this.x, this.y, 18);
  }

  update(dt, t) {
    if (this.state === "popping") {
      this.popT += dt;
      this.alpha = Math.max(0, 1 - this.popT / 0.3);
      this.sx = this.sy = 1 + this.popT;
      if (this.popT >= 0.3) this.state = "gone";
      return;
    }

    let speed = this.speed * u;
    if (this.state === "arriving") speed = Math.max(speed, 90 * u) * 2;
    if (this.state === "leaving") speed = Math.max(speed, 80 * u) * 2.5;

    if (this.habitat === "bottomCrawler") {
      if (this.state === "living") {
        if (t > this.nextPause) {
          this.pauseUntil = t + rand(1, 3.5);
          this.nextPause = this.pauseUntil + rand(4, 12);
        }
        if (t < this.pauseUntil) speed = 0;
      }
      this.swim(dt, t, speed);
      const step = speed > 0 ? Math.abs(Math.sin(t * (4 + this.speed * 0.08) + this.phase)) * 5 * u : 0;
      this.y = L.walkY + this.depth * u - this.h / 2 - step;
    } else if (this.habitat === "floater") {
      this.swim(dt, t, speed);
      const pulse = Math.sin(t * this.pulseSpeed + this.phase);
      this.sx = 1 + 0.045 * pulse;
      this.sy = 1 - 0.06 * pulse;
      this.y = this.baseY + Math.sin(t * 0.23 + this.phase) * 30 * u - Math.max(0, pulse) * 6 * u;
    } else {
      this.swim(dt, t, speed);
      if (this.state === "living" && t > this.nextDepth) {
        if (this.nextDepth > 0) this.targetY = this.homeY();
        this.nextDepth = t + rand(6, 16);
      }
      this.baseY += (this.targetY - this.baseY) * Math.min(1, dt * 0.4);
      this.y = this.baseY + Math.sin(t * this.bobSpeed + this.phase) * this.bobAmp * u;
    }

    // Easing facing through zero gives a quick "turning around" squash instead of a hard flip.
    const wanted = this.habitat === "floater" ? 1 : this.dir;
    this.facing += (wanted - this.facing) * Math.min(1, dt * 5);
  }

  swim(dt, t, speed) {
    this.x += this.dir * speed * dt;

    if (this.state === "arriving") {
      if (this.x > this.minX && this.x < this.maxX) {
        this.state = "living";
        this.tagFullUntil = performance.now() + TAG_FULL_MS;
      }
      return;
    }
    if (this.state === "leaving") {
      if (this.x < -this.sw / 2 || this.x > L.tankRight + this.sw / 2) this.state = "gone";
      return;
    }

    if (this.nextTurn === 0) this.nextTurn = t + rand(8, 24);
    if (this.x >= this.maxX && this.dir > 0) this.dir = -1;
    else if (this.x <= this.minX && this.dir < 0) this.dir = 1;
    else if (t > this.nextTurn) {
      this.dir = -this.dir;
      this.nextTurn = t + rand(8, 24);
    }
  }

  draw(g, t) {
    const s = this.sprite;
    if (!s) return;
    g.save();
    g.globalAlpha = this.alpha;
    g.translate(this.x, this.y);
    const flip = Math.abs(this.facing) < 0.06 ? 0.06 * Math.sign(this.facing || 1) : this.facing;
    g.scale(flip * this.sx, this.sy);

    const amp = this.waveAmp * this.h;
    if (this.habitat === "floater") {
      // Horizontal slices sway sideways, more toward the bottom, like trailing tentacles.
      const n = 16;
      for (let j = 0; j < n; j++) {
        const y0 = Math.floor((j * s.height) / n);
        const y1 = j === n - 1 ? s.height : Math.floor(((j + 1) * s.height) / n);
        const off = amp * Math.pow(j / (n - 1), 1.5) * Math.sin(t * this.waveSpeed - j * 0.45 + this.phase);
        g.drawImage(s, 0, y0, s.width, y1 - y0, -this.sw / 2 + off, -this.sh / 2 + y0 / dpr, this.sw, (y1 - y0) / dpr + 0.6);
      }
    } else {
      // Vertical slices ripple up and down, strongest at the tail (the left: drawings face right).
      const n = 20;
      for (let i = 0; i < n; i++) {
        const x0 = Math.floor((i * s.width) / n);
        const x1 = i === n - 1 ? s.width : Math.floor(((i + 1) * s.width) / n);
        const off = amp * Math.pow(1 - i / (n - 1), 1.6) * Math.sin(t * this.waveSpeed + i * 0.5 + this.phase);
        g.drawImage(s, x0, 0, x1 - x0, s.height, -this.sw / 2 + x0 / dpr, -this.sh / 2 + off, (x1 - x0) / dpr + 0.6, this.sh);
      }
    }
    g.restore();
  }

  /** Name tag: "{creature} by {artist}" for a few seconds after arrival, then just the creature name, faintly. */
  drawTag(g, now) {
    // Still swimming in from off-screen (or already gone): no tag stuck to the edge.
    if (this.x < -this.w / 4 || this.x > L.tankRight + this.w / 4) return;
    const full = this.state === "arriving" ? 1 : now < this.tagFullUntil ? 1 : clamp(1 - (now - this.tagFullUntil) / TAG_SHRINK_MS, 0, 1);
    // Crawlers walk along the bottom edge, so their tag goes above them instead of below.
    const above = this.habitat === "bottomCrawler";
    const edge = above ? this.y - (this.h / 2) * this.sy - 6 * u : this.y + (this.h / 2) * this.sy + 6 * u;
    const tagY = (height) => (above ? edge - height : edge);
    g.save();
    g.textAlign = "center";
    g.textBaseline = "top";

    if (full < 1) {
      g.globalAlpha = this.alpha * 0.6 * (1 - full);
      g.font = `600 ${17 * u}px ${theme.fontBody}`;
      const text = ellipsize(g, this.name, 260 * u);
      const half = g.measureText(text).width / 2;
      g.shadowColor = "rgba(0, 0, 0, 0.9)";
      g.shadowBlur = 4 * u;
      g.fillStyle = theme.text;
      g.fillText(text, clamp(this.x, half + 6 * u, L.tankRight - half - 6 * u), tagY(20 * u));
    }

    if (full > 0) {
      g.globalAlpha = this.alpha * full;
      g.font = `700 ${25 * u}px ${theme.fontBody}`;
      const nameText = ellipsize(g, this.name, 330 * u);
      g.font = `500 ${25 * u}px ${theme.fontBody}`;
      const byText = ellipsize(g, ` by ${this.artist}`, 280 * u);
      const byW = g.measureText(byText).width;
      g.font = `700 ${25 * u}px ${theme.fontBody}`;
      const nameW = g.measureText(nameText).width;
      const padX = 16 * u;
      const w = nameW + byW + padX * 2;
      const h = 42 * u;
      const x = clamp(this.x, w / 2 + 6 * u, L.tankRight - w / 2 - 6 * u) - w / 2;
      const y = tagY(h);
      g.fillStyle = "rgba(2, 14, 28, 0.82)";
      g.strokeStyle = GLOW_BY_HABITAT[this.habitat];
      g.lineWidth = 2 * u;
      roundRect(g, x, y, w, h, h / 2);
      g.fill();
      g.stroke();
      g.textAlign = "left";
      g.fillStyle = theme.text;
      g.fillText(nameText, x + padX, y + 8 * u);
      g.font = `500 ${25 * u}px ${theme.fontBody}`;
      g.fillStyle = theme.muted;
      g.fillText(byText, x + padX + nameW, y + 8 * u);
    }
    g.restore();
  }

  dispose() {
    if (this.sprite) this.sprite.width = this.sprite.height = 0;
    this.sprite = null;
    this.img = null;
  }
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.decoding = "async";
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error(`Could not load ${url}`));
    img.src = url;
  });
}

async function addCreature(data, arrive) {
  if (state.creatures.has(data.id) || state.loading.has(data.id)) return;
  state.loading.add(data.id);
  state.cancelled.delete(data.id);
  let img;
  try {
    img = await loadImage(data.imageUrl);
  } catch (err) {
    console.warn(err);
    return;
  } finally {
    state.loading.delete(data.id);
  }
  if (state.cancelled.delete(data.id)) return; // removed while the image was loading

  const creature = new Creature(data, img, arrive);
  state.creatures.set(creature.id, creature);
  if (arrive) burst(clamp(creature.x, 30 * u, L.tankRight - 30 * u), creature.y, 30);
  enforceCap();
}

function removeCreature(id, how) {
  if (state.loading.has(id)) state.cancelled.add(id);
  const c = state.creatures.get(id);
  if (how === "pop") {
    // Moderator removal: gone from the screen and the ticker right away.
    state.arrivals = state.arrivals.filter((a) => a.id !== id);
    c?.pop();
  } else {
    c?.leave();
  }
}

/** The server enforces the cap too; this keeps the screen right even if an event was missed. */
function enforceCap() {
  const resident = [...state.creatures.values()].filter((c) => c.state === "living" || c.state === "arriving");
  const extra = resident.length - state.config.populationCap;
  if (extra <= 0) return;
  resident.sort((a, b) => a.approvedAt - b.approvedAt);
  for (const c of resident.slice(0, extra)) c.leave();
}

function pushArrival(data) {
  state.arrivals = [{ id: data.id, name: data.creatureName, artist: data.artistDisplay }, ...state.arrivals.filter((a) => a.id !== data.id)].slice(0, TICKER_SIZE);
}

// ---------- Bubbles and activity ----------

function burst(x, y, count) {
  for (let i = 0; i < count; i++) {
    state.bubbles.push({
      x: x + rand(-40, 40) * u,
      y: y + rand(-30, 30) * u,
      r: rand(3, 11) * u,
      vy: rand(50, 140) * u,
      phase: rand(0, 6.28),
      wobble: rand(1, 3),
      age: 0,
      life: rand(1.6, 3.2),
    });
  }
  if (state.bubbles.length > MAX_BUBBLES) state.bubbles.splice(0, state.bubbles.length - MAX_BUBBLES);
}

const ACTIVITY_ROUTES = {
  connect: [["phone", "server", cyanDot, 0.6]],
  submit: [["phone", "server", cyanDot, 1]],
  approve: [["server", "screen", cyanDot, 1], ["server", "phone", coralDot, 0.8]],
  reject: [["server", "phone", coralDot, 0.8]],
  remove: [["server", "screen", coralDot, 0.8]],
  retire: [["server", "screen", coralDot, 0.6]],
};

function addActivity(kind) {
  const now = performance.now();
  for (const [from, to, sprite, scale] of ACTIVITY_ROUTES[kind] ?? []) {
    state.dots.push({ from, to, sprite, scale, start: now, dur: 900 });
  }
  if (state.dots.length > 40) state.dots.splice(0, state.dots.length - 40);
}

// ---------- Update ----------

function update(dt, t, now) {
  const s = scenery;
  for (const p of s.snow) {
    p.y += p.vy * dt;
    p.x += Math.sin(t * 0.3 + p.phase) * 6 * u * dt;
    if (p.y > H) {
      p.y = -5;
      p.x = Math.random() * W;
    }
  }
  for (const p of s.plankton) {
    p.x += p.vx * dt;
    if (p.x < 0) p.x += L.tankRight;
    if (p.x > L.tankRight) p.x -= L.tankRight;
  }
  const school = s.school;
  school.x += school.dir * school.speed * dt;
  if (school.x < -260 * u) school.x = L.tankRight + 40 * u;

  for (const vent of s.vents) {
    if (t > vent.next) {
      vent.next = t + rand(0.35, 1.3);
      state.bubbles.push({ x: vent.x + rand(-5, 5) * u, y: L.floorY - 40 * u, r: rand(3, 8) * u, vy: rand(45, 85) * u, phase: rand(0, 6.28), wobble: rand(1, 2.5), age: 0, life: Infinity });
    }
  }
  state.bubbles = state.bubbles.filter((b) => {
    b.age += dt;
    b.y -= b.vy * dt;
    b.x += Math.sin(t * b.wobble + b.phase) * 14 * u * dt;
    return b.y > -b.r && b.age < b.life;
  });

  for (const key of Object.keys(state.pulses)) state.pulses[key] = Math.max(0, state.pulses[key] - dt * 1.8);
  state.dots = state.dots.filter((d) => {
    if (now - d.start < d.dur) return true;
    state.pulses[d.to] = 1;
    return false;
  });

  for (const c of state.creatures.values()) {
    c.update(dt, t);
    if (c.state === "gone") {
      c.dispose();
      state.creatures.delete(c.id);
    }
  }
}

// ---------- Render ----------

function render(g, t, now, overlay) {
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.drawImage(staticLayer, 0, 0, W, H);
  paintRays(g, W, H * 0.85, t, Math.max(4, Math.round((5 * W) / 1920)));
  drawSchool(g, t);
  drawPlankton(g, t);
  drawKelp(g, t);
  drawSnow(g);

  g.save();
  g.beginPath();
  g.rect(0, 0, L.tankRight, H);
  g.clip();
  const creatures = [...state.creatures.values()].sort(
    (a, b) => HABITATS.indexOf(a.habitat) - HABITATS.indexOf(b.habitat) || a.approvedAt - b.approvedAt);
  for (const c of creatures) c.draw(g, t);
  drawBubbles(g);
  for (const c of creatures) c.drawTag(g, now);
  if (creatures.length === 0) drawEmptyMessage(g, t);
  g.restore();

  drawHeader(g);
  if (overlay) drawPanel(g, t, now);
  if (state.debug) drawDebug(g);
}

function drawSchool(g, t) {
  const school = scenery.school;
  g.save();
  g.fillStyle = "rgba(150, 195, 225, 0.14)";
  for (const f of school.fish) {
    const x = school.x + f.dx;
    const y = school.y + f.dy + Math.sin(t * 0.8 + f.phase) * 6 * u;
    g.beginPath();
    g.ellipse(x, y, 10 * u, 3.6 * u, 0, 0, Math.PI * 2);
    g.moveTo(x + 9 * u, y);
    g.lineTo(x + 16 * u, y - 4 * u);
    g.lineTo(x + 16 * u, y + 4 * u);
    g.fill();
  }
  g.restore();
}

function drawPlankton(g, t) {
  g.save();
  g.globalCompositeOperation = "lighter";
  for (const p of scenery.plankton) {
    g.globalAlpha = 0.25 + 0.35 * (0.5 + 0.5 * Math.sin(t * p.speed + p.phase));
    g.drawImage(planktonGlow, p.x - p.size / 2, p.y - p.size / 2, p.size, p.size);
  }
  g.restore();
}

function drawKelp(g, t) {
  const segments = 10;
  const baseY = L.floorY + 16 * u;
  g.save();
  g.globalAlpha = 0.9;
  for (const k of scenery.kelp) {
    const pts = [];
    for (let i = 0; i <= segments; i++) {
      const f = i / segments;
      pts.push([k.x + Math.sin(t * 0.7 + k.phase + f * 2.2) * 28 * u * f * f, baseY - f * k.h]);
    }
    g.beginPath();
    for (let i = 0; i <= segments; i++) {
      const half = (k.w * (1 - (i / segments) * 0.75)) / 2;
      g.lineTo(pts[i][0] - half, pts[i][1]);
    }
    for (let i = segments; i >= 0; i--) {
      const half = (k.w * (1 - (i / segments) * 0.75)) / 2;
      g.lineTo(pts[i][0] + half, pts[i][1]);
    }
    g.closePath();
    g.fillStyle = k.color;
    g.fill();
  }
  g.restore();
}

function drawSnow(g) {
  g.save();
  g.fillStyle = "#dceeff";
  for (const p of scenery.snow) {
    g.globalAlpha = p.alpha;
    g.fillRect(p.x, p.y, p.r * 1.6, p.r * 1.6);
  }
  g.restore();
}

function drawBubbles(g) {
  for (const b of state.bubbles) {
    const fade = b.life === Infinity ? 1 : clamp((b.life - b.age) / 0.6, 0, 1);
    paintBubble(g, b.x, b.y, b.r, 0.55 * fade);
  }
}

function drawEmptyMessage(g, t) {
  const cx = L.tankRight / 2;
  const cy = (L.headerH + L.floorY) / 2;
  g.save();
  g.textAlign = "center";
  g.globalAlpha = 0.75 + 0.25 * Math.sin(t * 1.5);
  g.fillStyle = theme.text;
  g.font = `700 ${46 * u}px ${theme.fontDisplay}`;
  g.fillText("The tank is empty… for now.", cx, cy);
  g.fillStyle = theme.glow;
  g.font = `600 ${28 * u}px ${theme.fontBody}`;
  g.fillText("Scan the code to release the first creature →", cx, cy + 50 * u);
  g.restore();
}

function setLetterSpacing(g, value) {
  if ("letterSpacing" in g) g.letterSpacing = value;
}

function drawHeader(g) {
  const x = 44 * u;
  const y = 34 * u;
  g.save();
  g.textAlign = "left";
  g.textBaseline = "middle";

  // "Computer Programming", displayed prominently above the title.
  const label = "COMPUTER PROGRAMMING";
  g.font = `700 ${24 * u}px ${theme.fontDisplay}`;
  setLetterSpacing(g, `${3 * u}px`);
  const bw = g.measureText(label).width + 32 * u;
  const bh = 42 * u;
  g.fillStyle = theme.glow;
  g.shadowColor = "rgba(70, 230, 255, 0.6)";
  g.shadowBlur = 18 * u;
  roundRect(g, x, y, bw, bh, bh / 2);
  g.fill();
  g.shadowBlur = 0;
  g.fillStyle = "#02121f";
  g.fillText(label, x + 16 * u, y + bh / 2 + 1 * u);
  setLetterSpacing(g, "0px");

  g.textBaseline = "top";
  g.font = `700 ${70 * u}px ${theme.fontDisplay}`;
  g.shadowColor = "rgba(70, 230, 255, 0.55)";
  g.shadowBlur = 26 * u;
  g.fillStyle = theme.text;
  g.fillText("Deep Sea Creature Lab", x, y + bh + 6 * u);
  g.restore();
}

function qrTarget() {
  const local = ["localhost", "127.0.0.1", "[::1]"].includes(location.hostname);
  const base = state.config.publicBaseUrl || (local ? "" : location.origin);
  return base ? `${base}/draw` : null;
}

function drawPanel(g, t, now) {
  const x0 = L.panelX;
  const w = L.panelW;
  const pad = 26 * u;
  const inner = w - pad * 2;
  const cx = x0 + w / 2;

  g.save();
  g.fillStyle = "rgba(2, 12, 26, 0.8)";
  g.fillRect(x0, 0, w, H);
  g.fillStyle = "rgba(70, 230, 255, 0.35)";
  g.fillRect(x0, 0, 2 * u, H);

  // Call to action and QR code
  let y = pad + 34 * u;
  g.textAlign = "center";
  g.textBaseline = "alphabetic";
  g.font = `800 ${36 * u}px ${theme.fontDisplay}`;
  g.fillStyle = theme.text;
  g.fillText("Scan to invent", cx, y);
  y += 42 * u;
  g.fillStyle = theme.glow;
  g.fillText("a creature!", cx, y);
  y += 18 * u;

  const qrSize = Math.min(inner, 270 * u);
  drawQrCard(g, cx - qrSize / 2, y, qrSize, t);
  y += qrSize + 30 * u;
  g.fillStyle = theme.muted;
  g.font = `500 ${18 * u}px ${theme.fontBody}`;
  g.fillText("No app needed · about a minute", cx, y);
  y += 34 * u;

  // How it works
  sectionTitle(g, "HOW IT WORKS", x0 + pad, y);
  y += 14 * u;
  drawDiagram(g, x0 + pad, y, inner, now);
  y += 126 * u;
  g.textAlign = "center";
  g.fillStyle = theme.muted;
  g.font = `500 ${16 * u}px ${theme.fontBody}`;
  const mpm = state.stats.messagesPerMinute ?? 0;
  g.fillText(`${mpm} message${mpm === 1 ? "" : "s"} in the last minute`, cx, y);
  y += 16 * u;

  const living = [...state.creatures.values()].filter((c) => c.state === "living" || c.state === "arriving").length;
  drawStats(g, x0 + pad, y, inner, now, [
    ["connectedPhones", state.stats.connectedPhones, "phones", "connected"],
    ["submissionsToday", state.stats.submissionsToday, "drawn", "today"],
    ["inTank", living, "in the", "tank"],
  ]);
  y += 96 * u + 30 * u;

  // Footer, laid out from the bottom up so it never collides with the list above it.
  g.font = `500 ${14 * u}px ${theme.fontBody}`;
  const credit = wrapLines(g, state.config.creditLine || "", inner, 3);
  g.font = `700 ${14 * u}px ${theme.fontBody}`;
  const programs = state.config.programs.flatMap((p) => wrapLines(g, p.label, inner, 2));
  const lineH = 19 * u;
  const titleGap = 34 * u; // space above and below the "Learn to build this" title
  const footerH = credit.length * lineH + (programs.length ? titleGap + programs.length * lineH : 0);
  const footerTop = H - pad - footerH;

  drawArrivals(g, x0 + pad, y, inner, footerTop - y - 8 * u);

  g.textAlign = "left";
  let fy = footerTop + 14 * u;
  g.fillStyle = theme.text;
  g.font = `500 ${14 * u}px ${theme.fontBody}`;
  for (const line of credit) {
    g.fillText(line, x0 + pad, fy);
    fy += lineH;
  }
  if (programs.length) {
    fy += 12 * u;
    sectionTitle(g, "LEARN TO BUILD THIS", x0 + pad, fy - 4 * u);
    fy += titleGap - 12 * u;
    g.fillStyle = theme.glow;
    g.font = `700 ${14 * u}px ${theme.fontBody}`;
    for (const line of programs) {
      g.fillText(line, x0 + pad, fy);
      fy += lineH;
    }
  }

  // Small status for the booth crew: only shown when something needs attention.
  g.textAlign = "right";
  g.font = `600 ${13 * u}px ${theme.fontBody}`;
  if (state.connection !== "connected") {
    g.fillStyle = theme.sun;
    g.fillText("● reconnecting to server…", x0 + w - 10 * u, H - 8 * u);
  } else if (now - state.lastSnapshotAt < 3000) {
    g.fillStyle = theme.muted;
    g.fillText("snapshot saved", x0 + w - 10 * u, H - 8 * u);
  }
  g.restore();
}

function sectionTitle(g, text, x, y) {
  g.save();
  g.textAlign = "left";
  g.textBaseline = "alphabetic";
  g.font = `700 ${14 * u}px ${theme.fontDisplay}`;
  setLetterSpacing(g, `${2.5 * u}px`);
  g.fillStyle = theme.glow;
  g.fillText(text, x, y);
  g.restore();
}

function drawQrCard(g, x, y, size, t) {
  g.save();
  g.shadowColor = "rgba(70, 230, 255, 0.7)";
  g.shadowBlur = (20 + 10 * Math.sin(t * 2)) * u;
  g.fillStyle = "#fff";
  roundRect(g, x, y, size, size, 18 * u);
  g.fill();
  g.restore();

  const url = qrTarget();
  if (!url) {
    g.save();
    g.textAlign = "center";
    g.fillStyle = "#02121f";
    g.font = `700 ${22 * u}px ${theme.fontBody}`;
    g.fillText("Waiting for the", x + size / 2, y + size / 2 - 10 * u);
    g.fillText("public URL…", x + size / 2, y + size / 2 + 18 * u);
    g.font = `500 ${15 * u}px ${theme.fontBody}`;
    g.fillStyle = "#4a6274";
    g.fillText("Set it on the admin page", x + size / 2, y + size / 2 + 48 * u);
    g.restore();
    return;
  }

  const inset = 10 * u;
  const inner = size - inset * 2;
  const key = `${url}|${Math.round(inner * dpr)}`;
  if (qrCache.key !== key) {
    const c = document.createElement("canvas");
    c.width = c.height = Math.round(inner * dpr);
    drawQr(c.getContext("2d"), url, 0, 0, c.width, { margin: 1 });
    qrCache = { key, canvas: c };
  }
  g.save();
  g.imageSmoothingEnabled = false;
  g.drawImage(qrCache.canvas, x + inset, y + inset, inner, inner);
  g.restore();
}

function drawDiagram(g, x, y, w, now) {
  const r = 30 * u;
  const cy = y + 14 * u + r;
  const nodes = {
    phone: { x: x + r + 4 * u, y: cy, label: "Phone" },
    server: { x: x + w / 2, y: cy, label: "Server" },
    screen: { x: x + w - r - 4 * u, y: cy, label: "Screen" },
  };

  g.save();
  g.strokeStyle = "rgba(140, 220, 255, 0.35)";
  g.lineWidth = 2 * u;
  g.setLineDash([6 * u, 6 * u]);
  g.beginPath();
  g.moveTo(nodes.phone.x + r + 6 * u, cy);
  g.lineTo(nodes.server.x - r - 6 * u, cy);
  g.moveTo(nodes.server.x + r + 6 * u, cy);
  g.lineTo(nodes.screen.x - r - 6 * u, cy);
  g.stroke();
  g.setLineDash([]);
  g.fillStyle = "rgba(140, 220, 255, 0.55)";
  for (const tip of [nodes.server.x - r - 6 * u, nodes.screen.x - r - 6 * u]) {
    g.beginPath();
    g.moveTo(tip, cy);
    g.lineTo(tip - 9 * u, cy - 6 * u);
    g.lineTo(tip - 9 * u, cy + 6 * u);
    g.fill();
  }

  for (const [name, node] of Object.entries(nodes)) {
    const pulse = state.pulses[name];
    if (pulse > 0) {
      g.strokeStyle = `rgba(70, 230, 255, ${pulse.toFixed(3)})`;
      g.lineWidth = 3 * u;
      g.beginPath();
      g.arc(node.x, node.y, r + (1 - pulse) * 14 * u, 0, Math.PI * 2);
      g.stroke();
    }
    g.fillStyle = "rgba(255, 255, 255, 0.07)";
    g.strokeStyle = "rgba(140, 220, 255, 0.4)";
    g.lineWidth = 2 * u;
    g.beginPath();
    g.arc(node.x, node.y, r, 0, Math.PI * 2);
    g.fill();
    g.stroke();
    drawIcon(g, name, node.x, node.y);
    g.fillStyle = theme.text;
    g.textAlign = "center";
    g.font = `600 ${15 * u}px ${theme.fontBody}`;
    g.fillText(node.label, node.x, cy + r + 22 * u);
  }

  for (const d of state.dots) {
    const p = clamp((now - d.start) / d.dur, 0, 1);
    const e = p < 0.5 ? 2 * p * p : 1 - Math.pow(-2 * p + 2, 2) / 2; // ease in-out
    const a = nodes[d.from];
    const b = nodes[d.to];
    const dir = Math.sign(b.x - a.x);
    const sx = a.x + dir * r;
    const ex = b.x - dir * r;
    const size = 26 * u * d.scale;
    // Return trips (server → phone) travel just below the line.
    const dy = d.to === "phone" ? 12 * u : 0;
    g.globalCompositeOperation = "lighter";
    g.drawImage(d.sprite, sx + (ex - sx) * e - size / 2, cy + dy - size / 2, size, size);
    g.globalCompositeOperation = "source-over";
  }
  g.restore();
}

function drawIcon(g, name, x, y) {
  g.save();
  g.strokeStyle = theme.text;
  g.fillStyle = theme.text;
  g.lineWidth = 2.5 * u;
  g.lineJoin = "round";
  if (name === "phone") {
    roundRect(g, x - 10 * u, y - 17 * u, 20 * u, 34 * u, 4 * u);
    g.stroke();
    g.beginPath();
    g.arc(x, y + 11 * u, 2 * u, 0, Math.PI * 2);
    g.fill();
  } else if (name === "server") {
    for (const dy of [-13, -2, 9]) {
      roundRect(g, x - 15 * u, y + dy * u, 30 * u, 8 * u, 2 * u);
      g.stroke();
      g.fillStyle = theme.ok;
      g.beginPath();
      g.arc(x + 9 * u, y + (dy + 4) * u, 1.6 * u, 0, Math.PI * 2);
      g.fill();
    }
  } else {
    roundRect(g, x - 18 * u, y - 14 * u, 36 * u, 23 * u, 3 * u);
    g.stroke();
    g.beginPath();
    g.moveTo(x, y + 9 * u);
    g.lineTo(x, y + 15 * u);
    g.moveTo(x - 8 * u, y + 16 * u);
    g.lineTo(x + 8 * u, y + 16 * u);
    g.stroke();
  }
  g.restore();
}

function drawStats(g, x, y, w, now, items) {
  const gap = 10 * u;
  const tw = (w - gap * 2) / 3;
  const h = 96 * u;
  items.forEach(([key, value, line1, line2], i) => {
    const prev = state.statChangedAt[key];
    if (!prev || prev.value !== value) state.statChangedAt[key] = { value, at: prev ? now : -Infinity };
    const flash = clamp(1 - (now - state.statChangedAt[key].at) / 800, 0, 1);

    const tx = x + i * (tw + gap);
    g.fillStyle = `rgba(70, 230, 255, ${(0.06 + flash * 0.25).toFixed(3)})`;
    roundRect(g, tx, y, tw, h, 14 * u);
    g.fill();
    g.textAlign = "center";
    g.fillStyle = theme.text;
    g.font = `800 ${(40 + flash * 6) * u}px ${theme.fontDisplay}`;
    g.fillText(String(value ?? "–"), tx + tw / 2, y + 46 * u);
    g.fillStyle = theme.muted;
    g.font = `600 ${14 * u}px ${theme.fontBody}`;
    g.fillText(line1, tx + tw / 2, y + 69 * u);
    g.fillText(line2, tx + tw / 2, y + 86 * u);
  });
}

function drawArrivals(g, x, y, w, maxH) {
  sectionTitle(g, "LATEST ARRIVALS", x, y);
  let ry = y + 30 * u;
  const rowH = 30 * u;
  g.save();
  g.textAlign = "left";
  if (state.arrivals.length === 0) {
    g.fillStyle = theme.muted;
    g.font = `500 ${18 * u}px ${theme.fontBody}`;
    g.fillText("Yours could be first!", x, ry);
  }
  for (const a of state.arrivals) {
    if (ry - y > maxH) break;
    g.font = `700 ${19 * u}px ${theme.fontBody}`;
    g.fillStyle = theme.text;
    const name = ellipsize(g, a.name, w * 0.62);
    const nameW = g.measureText(name).width;
    g.fillText(name, x, ry);
    g.font = `500 ${19 * u}px ${theme.fontBody}`;
    g.fillStyle = theme.muted;
    g.fillText(ellipsize(g, ` by ${a.artist}`, w - nameW), x + nameW, ry);
    ry += rowH;
  }
  g.restore();
}

function drawDebug(g) {
  const lines = [
    `${state.fps} fps, ${state.frameMs.toFixed(1)} ms per frame${document.hidden ? " (tab hidden: throttled)" : ""}`,
    `${state.creatures.size} creatures, ${state.bubbles.length} bubbles, ${state.dots.length} dots`,
    `${W}×${H} @${dpr}x`,
    performance.memory ? `heap ${(performance.memory.usedJSHeapSize / 1048576).toFixed(1)} MB` : "",
    `hub: ${state.connection}`,
  ].filter(Boolean);
  g.save();
  g.fillStyle = "rgba(0,0,0,0.7)";
  g.fillRect(8, H - 20 - lines.length * 18, 360, lines.length * 18 + 12);
  g.fillStyle = "#7dff6b";
  g.font = "14px monospace";
  g.textAlign = "left";
  lines.forEach((line, i) => g.fillText(line, 16, H - 12 - (lines.length - 1 - i) * 18));
  g.restore();
}

// ---------- Snapshots ----------

let snapshotTimer = null;
let snapshotMinutes = 0;

function setSnapshotInterval(minutes) {
  if (minutes === snapshotMinutes) return;
  snapshotMinutes = minutes;
  clearInterval(snapshotTimer);
  snapshotTimer = minutes > 0 ? setInterval(takeSnapshot, minutes * 60000) : null;
}

async function takeSnapshot() {
  if (document.hidden) return; // the canvas isn't updating while hidden
  try {
    let source = canvas;
    if (!state.config.snapshotIncludeOverlay) {
      source = document.createElement("canvas");
      source.width = canvas.width;
      source.height = canvas.height;
      const now = performance.now();
      render(source.getContext("2d"), now / 1000, now, false);
    }
    const blob = await new Promise((resolve) => source.toBlob(resolve, "image/png"));
    if (!blob) return;
    // Accepted only from the booth laptop (localhost); a copy of /tank opened elsewhere just gets a 401.
    const res = await fetch("/api/snapshots", { method: "POST", headers: { "Content-Type": "image/png" }, body: blob });
    if (res.ok) state.lastSnapshotAt = performance.now();
  } catch (err) {
    console.warn("Snapshot failed", err);
  }
}

// ---------- Server connection ----------

async function loadConfig() {
  const res = await fetch("/api/config");
  if (!res.ok) return;
  state.config = await res.json();
  setSnapshotInterval(state.config.snapshotIntervalMinutes);
}

/** Initial load, and reconciliation after a reconnect (anything missed while offline). */
async function loadTank() {
  const res = await fetch("/api/tank");
  if (!res.ok) return;
  const list = await res.json();
  const ids = new Set(list.map((c) => c.id));
  for (const c of state.creatures.values()) {
    if (!ids.has(c.id) && (c.state === "living" || c.state === "arriving")) c.pop();
  }
  for (const id of state.loading) if (!ids.has(id)) state.cancelled.add(id);
  state.arrivals = list.slice(-TICKER_SIZE).reverse().map((c) => ({ id: c.id, name: c.creatureName, artist: c.artistDisplay }));
  await Promise.all(list.map((c) => addCreature(c, false)));
}

const hub = createHub();
hub.on("CreatureApproved", (c) => {
  pushArrival(c);
  addCreature(c, true);
});
hub.on("CreatureRemoved", ({ id }) => removeCreature(id, "pop"));
hub.on("CreatureRetired", ({ id }) => removeCreature(id, "leave"));
// Handlers must not return a value: SignalR would treat it as a reply to the server.
hub.on("Stats", (s) => {
  state.stats = s;
});
hub.on("Activity", (a) => addActivity(a.kind));
hub.on("TakeSnapshot", () => {
  takeSnapshot();
});
hub.on("SettingsChanged", (s) => {
  state.config.publicBaseUrl = s.publicBaseUrl;
  state.config.snapshotIntervalMinutes = s.snapshotIntervalMinutes;
  setSnapshotInterval(s.snapshotIntervalMinutes);
});

// ---------- Keep the TV awake, tidy, and running ----------

let wakeLock = null;
async function keepAwake() {
  try {
    wakeLock = await navigator.wakeLock?.request("screen");
  } catch {
    // Not supported or not allowed; set the laptop's power settings instead (see README).
  }
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden && (!wakeLock || wakeLock.released)) keepAwake();
});

let idleTimer = null;
window.addEventListener("pointermove", () => {
  document.body.classList.remove("idle");
  clearTimeout(idleTimer);
  idleTimer = setTimeout(() => document.body.classList.add("idle"), 3000);
});

window.addEventListener("keydown", (e) => {
  if (e.key === "f" || e.key === "F") {
    if (document.fullscreenElement) document.exitFullscreen();
    else document.documentElement.requestFullscreen?.().catch(() => {});
  } else if (e.key === "d" || e.key === "D") {
    state.debug = !state.debug;
  }
});
canvas.addEventListener("dblclick", () => document.documentElement.requestFullscreen?.().catch(() => {}));
window.addEventListener("resize", resize);

let last = performance.now();
let frames = 0;
let fpsSince = last;
function frame(now) {
  const dt = Math.min(0.05, (now - last) / 1000); // a hidden tab shouldn't make everything jump
  last = now;
  frames++;
  if (now - fpsSince >= 1000) {
    state.fps = frames;
    frames = 0;
    fpsSince = now;
  }
  const workStart = performance.now();
  update(dt, now / 1000, now);
  render(ctx, now / 1000, now, true);
  state.frameMs = state.frameMs * 0.9 + (performance.now() - workStart) * 0.1;
  requestAnimationFrame(frame);
}

resize();
keepAwake();
requestAnimationFrame(frame);
keepConnected(
  hub,
  async () => {
    await hub.invoke("JoinDisplay");
    await loadConfig();
    await loadTank();
  },
  (s) => (state.connection = s),
);
