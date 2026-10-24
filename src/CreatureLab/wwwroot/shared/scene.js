// Deep-sea painting shared by the tank (animated) and the keepsake card (still).

export function paintWater(ctx, x, y, w, h) {
  const g = ctx.createLinearGradient(0, y, 0, y + h);
  g.addColorStop(0, "#0d5288");
  g.addColorStop(0.28, "#07305a");
  g.addColorStop(0.68, "#041a33");
  g.addColorStop(1, "#020b16");
  ctx.fillStyle = g;
  ctx.fillRect(x, y, w, h);
}

/**
 * Faint light rays slanting down from the surface. `t` (seconds) makes them sway and
 * breathe; pass a fixed value for a still picture.
 */
export function paintRays(ctx, w, h, t, count = 5) {
  ctx.save();
  ctx.globalCompositeOperation = "lighter";
  for (let i = 0; i < count; i++) {
    const cx = w * (0.08 + ((i + 0.5) / count) * 0.84) + Math.sin(t * 0.07 + i * 2.1) * w * 0.025;
    const topW = w * (0.02 + 0.012 * (i % 3));
    const bottomW = topW * 5;
    const skew = w * 0.1 * Math.sin(i * 1.3 + 0.6) + Math.sin(t * 0.11 + i) * w * 0.015;
    const alpha = 0.05 + 0.03 * Math.sin(t * 0.25 + i * 1.7);

    const grad = ctx.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, `rgba(160, 225, 255, ${alpha.toFixed(3)})`);
    grad.addColorStop(1, "rgba(160, 225, 255, 0)");
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.moveTo(cx - topW / 2, 0);
    ctx.lineTo(cx + topW / 2, 0);
    ctx.lineTo(cx + skew + bottomW / 2, h);
    ctx.lineTo(cx + skew - bottomW / 2, h);
    ctx.closePath();
    ctx.fill();
  }
  ctx.restore();
}

/** Still marine snow: tiny pale specks, denser near the top. */
export function paintSnow(ctx, w, h, random, count) {
  ctx.save();
  for (let i = 0; i < count; i++) {
    const x = random() * w;
    const y = Math.pow(random(), 1.4) * h;
    const r = 0.8 + random() * 2.2;
    ctx.globalAlpha = 0.15 + random() * 0.45;
    ctx.fillStyle = "#dceeff";
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.restore();
}

/** A single bubble: thin outline with a highlight. */
export function paintBubble(ctx, x, y, r, alpha = 0.6) {
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = "rgba(200, 240, 255, 0.85)";
  ctx.lineWidth = Math.max(1, r * 0.14);
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.stroke();
  ctx.fillStyle = "rgba(200, 240, 255, 0.12)";
  ctx.fill();
  ctx.fillStyle = "rgba(255, 255, 255, 0.9)";
  ctx.beginPath();
  ctx.arc(x - r * 0.35, y - r * 0.35, Math.max(0.8, r * 0.18), 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
}
