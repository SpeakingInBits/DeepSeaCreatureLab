// QR codes drawn straight onto a canvas, using the vendored qrcode-generator library.
/* global qrcode */

/**
 * Draws a QR code for `text` as a white square of `size` pixels at (x, y),
 * including a quiet zone of `margin` modules. Modules snap to whole pixels so
 * the code stays crisp (and scannable) at any size.
 */
export function drawQr(ctx, text, x, y, size, { margin = 2, dark = "#000", light = "#fff" } = {}) {
  const qr = qrcode(0, "M");
  qr.addData(text);
  qr.make();

  const count = qr.getModuleCount();
  const cells = count + margin * 2;
  const cell = Math.max(1, Math.floor(size / cells));
  const offset = Math.floor((size - cell * cells) / 2) + margin * cell;

  ctx.fillStyle = light;
  ctx.fillRect(x, y, size, size);
  ctx.fillStyle = dark;
  for (let row = 0; row < count; row++) {
    for (let col = 0; col < count; col++) {
      if (qr.isDark(row, col)) {
        ctx.fillRect(Math.round(x + offset + col * cell), Math.round(y + offset + row * cell), cell, cell);
      }
    }
  }
}
