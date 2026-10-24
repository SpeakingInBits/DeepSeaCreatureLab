// The "Save to my phone" keepsake card: a 1080×1350 PNG made entirely in the browser,
// so it works even if the server or tunnel hiccups (DESIGN.md §8.3.1).
import { theme } from "/shared/theme.js";
import { paintWater, paintRays, paintSnow, paintBubble } from "/shared/scene.js";
import { drawQr } from "/shared/qr.js";
import { seededRandom, hashString, roundRect, fitFont, wrapLines, ellipsize } from "/shared/canvas.js";

const W = 1080;
const H = 1350;
const FOOTER_TOP = 920;
const FOOTER_TEXT = "Deep Sea Creature Lab · Computer Programming · Program Expo 2026";

/**
 * @param {HTMLCanvasElement} drawing  the cropped creature (transparent background)
 * @param {string} creatureName
 * @param {string} artistDisplay       artist name, or "a mystery diver"
 * @param {{label: string, url: string}[]} programs
 * @returns {HTMLCanvasElement}
 */
export function renderKeepsakeCard({ drawing, creatureName, artistDisplay, programs }) {
  const canvas = document.createElement("canvas");
  canvas.width = W;
  canvas.height = H;
  const ctx = canvas.getContext("2d");
  const random = seededRandom(hashString(creatureName + artistDisplay));

  // Scene
  paintWater(ctx, 0, 0, W, H);
  paintRays(ctx, W, H * 0.85, random() * 20, 5);
  paintSnow(ctx, W, FOOTER_TOP, random, 140);
  for (let i = 0; i < 9; i++) {
    paintBubble(ctx, 60 + random() * (W - 120), 140 + random() * 620, 6 + random() * 16, 0.35 + random() * 0.3);
  }

  const halo = ctx.createRadialGradient(W / 2, 420, 30, W / 2, 420, 460);
  halo.addColorStop(0, "rgba(70, 230, 255, 0.22)");
  halo.addColorStop(1, "rgba(70, 230, 255, 0)");
  ctx.fillStyle = halo;
  ctx.fillRect(0, 0, W, FOOTER_TOP);

  // Top label
  ctx.textAlign = "center";
  ctx.textBaseline = "alphabetic";
  ctx.fillStyle = theme.glow;
  ctx.font = `700 30px ${theme.fontDisplay}`;
  setLetterSpacing(ctx, "6px");
  ctx.fillText("DEEP SEA CREATURE LAB", W / 2, 86);
  setLetterSpacing(ctx, "0px");

  // Creature, fitted into its box, with a soft glow
  const box = { x: 110, y: 130, w: 860, h: 540 };
  const scale = Math.min(box.w / drawing.width, box.h / drawing.height, 2.5);
  const dw = drawing.width * scale;
  const dh = drawing.height * scale;
  const dx = box.x + (box.w - dw) / 2;
  const dy = box.y + (box.h - dh) / 2;
  ctx.save();
  ctx.shadowColor = "rgba(120, 220, 255, 0.75)";
  ctx.shadowBlur = 28;
  ctx.drawImage(drawing, dx, dy, dw, dh);
  ctx.restore();
  ctx.drawImage(drawing, dx, dy, dw, dh);

  // Name and signature
  ctx.fillStyle = theme.text;
  ctx.shadowColor = "rgba(0, 0, 0, 0.6)";
  ctx.shadowBlur = 12;
  fitFont(ctx, creatureName, 960, 100, 54, (s) => `800 ${s}px ${theme.fontDisplay}`);
  ctx.fillText(ellipsize(ctx, creatureName, 960), W / 2, 790);
  ctx.fillStyle = theme.muted;
  fitFont(ctx, `drawn by ${artistDisplay}`, 960, 44, 30, (s) => `500 ${s}px ${theme.fontBody}`);
  ctx.fillText(ellipsize(ctx, `drawn by ${artistDisplay}`, 960), W / 2, 856);
  ctx.shadowBlur = 0;

  // Footer band
  ctx.fillStyle = "rgba(1, 8, 18, 0.78)";
  ctx.fillRect(0, FOOTER_TOP, W, H - FOOTER_TOP);
  ctx.fillStyle = "rgba(70, 230, 255, 0.5)";
  ctx.fillRect(0, FOOTER_TOP, W, 2);

  ctx.fillStyle = theme.text;
  fitFont(ctx, FOOTER_TEXT, 1000, 27, 18, (s) => `600 ${s}px ${theme.fontBody}`);
  ctx.fillText(FOOTER_TEXT, W / 2, FOOTER_TOP + 48);

  // Program links: printed short URL plus a QR code each (links in an image aren't clickable)
  const shown = (programs || []).filter((p) => /^https?:\/\//i.test(p.url)).slice(0, 2);
  const colW = W / Math.max(1, shown.length);
  shown.forEach((program, i) => {
    const cx = colW * i + colW / 2;
    const qrSize = 190;
    const qrY = FOOTER_TOP + 76;
    ctx.fillStyle = "#fff";
    roundRect(ctx, cx - qrSize / 2 - 8, qrY - 8, qrSize + 16, qrSize + 16, 14);
    ctx.fill();
    drawQr(ctx, program.url, cx - qrSize / 2, qrY, qrSize, { margin: 1 });

    let y = qrY + qrSize + 42;
    ctx.fillStyle = theme.text;
    ctx.font = `700 24px ${theme.fontBody}`;
    for (const line of wrapLines(ctx, program.label, colW - 60, 2)) {
      ctx.fillText(line, cx, y);
      y += 30;
    }
    ctx.fillStyle = theme.glow;
    fitFont(ctx, shortUrl(program.url), colW - 40, 22, 14, (s) => `600 ${s}px ${theme.fontBody}`);
    ctx.fillText(shortUrl(program.url), cx, y + 4);
  });

  return canvas;
}

export function shortUrl(url) {
  return url.replace(/^https?:\/\/(www\.)?/i, "").replace(/\/$/, "");
}

function setLetterSpacing(ctx, value) {
  if ("letterSpacing" in ctx) ctx.letterSpacing = value;
}
