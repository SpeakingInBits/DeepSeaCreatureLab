// Small canvas helpers shared by the tank and the keepsake card.

/** Seeded random numbers (mulberry32), so a scene can be repainted the same way. */
export function seededRandom(seed) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function hashString(text) {
  let h = 2166136261;
  for (let i = 0; i < text.length; i++) h = Math.imul(h ^ text.charCodeAt(i), 16777619);
  return h >>> 0;
}

export function roundRect(ctx, x, y, w, h, r) {
  ctx.beginPath();
  if (ctx.roundRect) {
    ctx.roundRect(x, y, w, h, r);
    return;
  }
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
}

/** Cuts text down with "…" until it fits maxWidth (uses the current ctx.font). */
export function ellipsize(ctx, text, maxWidth) {
  if (ctx.measureText(text).width <= maxWidth) return text;
  const chars = Array.from(text);
  while (chars.length > 1 && ctx.measureText(chars.join("") + "…").width > maxWidth) chars.pop();
  return chars.join("") + "…";
}

/**
 * Picks the largest font size (from max down to min) at which text fits maxWidth.
 * `font(size)` returns a CSS font string. Sets ctx.font and returns the size.
 */
export function fitFont(ctx, text, maxWidth, max, min, font) {
  let size = max;
  ctx.font = font(size);
  while (size > min && ctx.measureText(text).width > maxWidth) {
    size -= 2;
    ctx.font = font(size);
  }
  return size;
}

/** Word-wraps text into at most maxLines lines (uses the current ctx.font). */
export function wrapLines(ctx, text, maxWidth, maxLines) {
  const words = text.split(/\s+/).filter(Boolean);
  const lines = [];
  let line = "";
  for (const word of words) {
    const next = line ? `${line} ${word}` : word;
    if (ctx.measureText(next).width <= maxWidth || !line) {
      line = next;
    } else {
      lines.push(line);
      line = word;
    }
  }
  if (line) lines.push(line);

  if (lines.length > maxLines) {
    const kept = lines.slice(0, maxLines);
    kept[maxLines - 1] = ellipsize(ctx, `${kept[maxLines - 1]} ${lines.slice(maxLines).join(" ")}`, maxWidth);
    return kept;
  }
  return lines.map((l) => ellipsize(ctx, l, maxWidth));
}

/** A soft round glow, pre-rendered once and stamped with drawImage (much cheaper than shadowBlur). */
export function makeGlow(color, radius) {
  const c = document.createElement("canvas");
  c.width = c.height = Math.ceil(radius * 2);
  const g = c.getContext("2d");
  const grad = g.createRadialGradient(radius, radius, 0, radius, radius, radius);
  grad.addColorStop(0, color);
  grad.addColorStop(1, "rgba(0,0,0,0)");
  g.fillStyle = grad;
  g.fillRect(0, 0, c.width, c.height);
  return c;
}
