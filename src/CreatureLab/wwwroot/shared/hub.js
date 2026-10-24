// SignalR connection setup shared by all three pages, so they all get the same rules:
// WebSockets first, long polling as the fallback, never Server-Sent Events (Cloudflare
// quick tunnels don't support them), and reconnect forever (the TV runs all day).
/* global signalR */

const RETRY_DELAYS_MS = [0, 1000, 2000, 5000, 10000];

export function createHub() {
  return new signalR.HubConnectionBuilder()
    .withUrl("/hubs/tank", {
      transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
    })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) =>
        RETRY_DELAYS_MS[Math.min(ctx.previousRetryCount, RETRY_DELAYS_MS.length - 1)],
    })
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

/**
 * Starts the connection, retrying until it works. Calls onConnected after the first
 * start and after every reconnect: group membership doesn't survive a reconnect, so
 * that's where each page re-joins its group and refreshes its data.
 * onState receives "connecting" | "connected" | "reconnecting" | "disconnected".
 */
export function keepConnected(connection, onConnected, onState = () => {}) {
  let starting = false;

  async function connected() {
    onState("connected");
    try {
      await onConnected();
    } catch (err) {
      console.warn("After-connect step failed", err);
    }
  }

  async function start() {
    if (starting) return;
    starting = true;
    for (let attempt = 0; ; attempt++) {
      try {
        onState("connecting");
        await connection.start();
        break;
      } catch (err) {
        onState("disconnected");
        console.warn("Hub connection failed; retrying", err);
        await sleep(Math.min(10000, 1000 * 2 ** attempt));
      }
    }
    starting = false;
    await connected();
  }

  connection.onreconnecting(() => onState("reconnecting"));
  connection.onreconnected(connected);
  connection.onclose(() => {
    onState("disconnected");
    start();
  });

  start();
}

export function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
