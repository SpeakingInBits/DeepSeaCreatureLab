# Deep Sea Creature Lab

A shared, live deep-sea tank for the Program Expo (Wed Oct 28, 2026, 3–6 PM). Visitors scan the QR
code on the TV, draw a creature on their phone, name it, sign it, and release it. After a crew member
approves it, it swims onto the big screen. A side panel shows the system working in real time.

![A visitor draws an anglerfish on their phone and releases it; a crew member approves it on the
admin page and it swims onto the TV; then the crew removes a creature and changes
settings](docs/walkthrough.gif)

One ASP.NET Core app on the booth laptop serves everything; phones reach it through a tunnel.
See [DESIGN.md](DESIGN.md) for the full design.

| Page | Who | URL |
|---|---|---|
| Draw | Visitors' phones | `https://<tunnel>/draw` (the QR code on the TV) |
| Tank | The TV, plugged into the laptop | `http://localhost:5000/tank` |
| Admin | Crew phones or the laptop | `https://<tunnel>/admin` or `http://localhost:5000/admin` |

## Run it locally

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or a newer SDK; the
app targets `net10.0`).

```powershell
dotnet run --project src/CreatureLab
```

Then open <http://localhost:5000/tank>, <http://localhost:5000/draw> and
<http://localhost:5000/admin>. The console prints the admin PIN and where data is stored.

For the event, use the `event` profile. It runs in Production mode, so error pages don't show stack
traces to visitors:

```powershell
dotnet run --project src/CreatureLab --launch-profile event
```

Run the tests:

```powershell
dotnet test
```

### Admin PIN

The PIN is never shown on a page or committed. Pick one of these:

- **Recommended:** create `src/CreatureLab/appsettings.Local.json` (gitignored):

  ```json
  { "Admin": { "Pin": "choose-6-plus-digits" } }
  ```

- Or set the environment variable `Admin__Pin` before starting the server.
- If you set neither, the server makes up a 6-digit PIN, saves it in `data/admin-pin.txt` (so it
  survives restarts) and prints it in the console.

After 5 wrong PINs in 10 minutes from one IP address, that address is locked out for 10 minutes.
The laptop itself (localhost) is never locked out.

### Public URL (for the QR code)

The TV's QR code points at `PublicBaseUrl + "/draw"`. Set it in either place:

- **Admin → Settings → Public URL.** Paste the tunnel URL and press Save. The QR code updates
  right away; no restart. Pasting a URL that ends in `/draw` is fine.
- `PublicBaseUrl` in `appsettings.json` or the `PublicBaseUrl` environment variable (useful for a
  permanent named tunnel).

Values saved on the admin page go into `data/settings.json` and win over `appsettings.json` on the
next start. Auto-approve is deliberately not saved: it always starts **off** after a restart.

### Settings

All in `src/CreatureLab/appsettings.json`. Any of them can be overridden with an environment
variable, using `__` for nesting (for example `$env:RateLimit__MaxPerIpPerMinute = 1000` in
PowerShell before `dotnet run`).

| Setting | Default | What it does |
|---|---|---|
| `PublicBaseUrl` | empty | Tunnel URL for the QR code (also editable on the admin page) |
| `Admin:Pin` | empty | Moderator PIN (see above) |
| `Tank:PopulationCap` | 30 | Creatures on screen; the oldest swims away when a new one arrives |
| `Moderation:AutoApprove` | false | Starting value of the admin page's auto-approve switch |
| `Moderation:MaxPending` | 100 | Queue limit, to protect the moderator |
| `Moderation:ProfanityListPath` | `profanity.txt` | Blocked words for names (format explained at the top of the file) |
| `RateLimit:SecondsPerDevice` | 30 | One release per phone every N seconds |
| `RateLimit:RequireConnection` | true | Only accept releases from a phone connected to the server (now or in the last 2 minutes); the draw page always is |
| `RateLimit:MaxPerIpPerMinute` | 20 | Backstop per network (campus Wi-Fi shares one IP), 0 turns it off |
| `Upload:MaxBytes` / `MaxDimension` / `MaxNameLength` | 256 KB / 512 px / 20 | Upload limits |
| `Storage:DataPath` | `../../data` | Where drawings, metadata and snapshots go (relative to `src/CreatureLab`) |
| `Snapshots:IntervalMinutes` | 5 | Timed tank snapshots for the time-lapse; 0 turns them off |
| `Snapshots:IncludeOverlay` | true | Include the QR code and side panel in snapshots |
| `Display:CreditLine` | "Built by Computer Programming with C#, ASP.NET Core, and SignalR." | Line on the TV panel |
| `Programs` | two CPTC degree links | Shown after release, on the keepsake card (with QR codes), on the TV, and in the gallery |

## Tunnels

Phones aren't on the laptop's network, so a tunnel gives the laptop a public HTTPS address. The
tunnel client runs on the laptop and forwards to `http://localhost:5000`. Leave its window open all
event; closing it kills the URL.

### A. Microsoft Dev Tunnels (primary)

Set this up **before** event day. The tunnel keeps the same URL every time it starts.

```powershell
winget install Microsoft.devtunnel
```

One-time setup (log in with a Microsoft or GitHub account):

```powershell
devtunnel user login
```

```powershell
devtunnel create creature-lab
```

```powershell
devtunnel port create creature-lab -p 5000
```

On the morning of the event (anonymous access expires; 12 hours lasts until 6 PM even if you create it early):

```powershell
devtunnel access create creature-lab -a --expiration 12h
```

Start it:

```powershell
devtunnel host creature-lab
```

Copy the `https://….devtunnels.ms` URL it prints and paste it into **Admin → Settings → Public
URL**. Visitors see a one-time "Continue" page; tell them "tap Continue, it's our tunnel."

### B. Cloudflare quick tunnel (backup)

No account needed, and visitors go straight to the page, but the URL changes every time it starts.
Install `cloudflared` once:

```powershell
winget install --id Cloudflare.cloudflared
```

To switch over: stop the Dev Tunnel (Ctrl+C), run the command below, and paste the
`https://….trycloudflare.com` URL it prints into the admin page. If cloudflared restarts, paste the
new URL again.

```powershell
cloudflared tunnel --url http://localhost:5000
```

### Notes for both tunnels

- The SignalR client uses WebSockets with long-polling as the fallback, never Server-Sent Events
  (quick tunnels don't support SSE).
- Forwarded headers (`X-Forwarded-For`, `X-Forwarded-Proto`) are trusted from the tunnel client on
  localhost, so rate limiting sees each visitor's real IP.
- Snapshot uploads are accepted only from the laptop itself, so a copy of `/tank` opened elsewhere
  can't add pictures.
- If venue internet fails, put the laptop on a phone hotspot. Both tunnels work over a hotspot.

## The TV

Plug the TV or monitor into the laptop and open the tank in kiosk (fullscreen) mode:

```powershell
Start-Process msedge "--kiosk http://localhost:5000/tank --edge-kiosk-type=fullscreen --no-first-run"
```

(or `chrome --kiosk http://localhost:5000/tank`). Close every Edge window first, or the flags are
ignored. Press Alt+F4 to leave kiosk mode.

- Works at 1920×1080 and on ultrawide screens (the tank gets wider; the QR code and panel stay on
  the right).
- **F** toggles fullscreen, double-click also enters it. **D** shows a debug overlay (fps, time per
  frame, memory, connection), which is useful for the soak test.
- The page reconnects on its own after a server or network hiccup, and reloads the tank from the
  server when it does. A page reload is always safe too.
- The page asks the browser to keep the screen awake, but also set the laptop's power settings:
  never sleep, never turn off the display, while plugged in.

## Testing

| What | How |
|---|---|
| Unit and API tests | `dotnet test` (store, PNG validation, rate limits, name filter, settings, PIN lockout, every endpoint) |
| Load test | `dotnet run tools/LoadTest.cs -- --url <url> --phones 50 --minutes 3 --pin <pin>` |
| Soak test | Same script with `--phones 500 --minutes 240`, with `/tank` open and **D** pressed to watch fps and memory |
| Phones | iPhone (Safari) and Android (Chrome) **on cellular**, through the tunnel |

The load test simulates phones from one IP address, so start the server with a higher network limit
first:

```powershell
$env:RateLimit__MaxPerIpPerMinute = 1000; dotnet run --project src/CreatureLab
```

Each simulated phone connects to the server before releasing, as the draw page does. The script
reports how many phones heard their result back over SignalR and the submit latency. The variable
stays set for that PowerShell window, so start the event server from a new one.

To try the draw page from a phone on the same Wi-Fi without a tunnel, start the server with
`--urls http://0.0.0.0:5000` and browse to the laptop's IP. Over plain http, "Save to my phone" falls
back to press-and-hold. The tunnel's HTTPS gives the full share sheet.

## Event-day checklist

### Tuesday 10/27: dress rehearsal

- [ ] Create the Dev Tunnel access token, start the Dev Tunnel, set the URL, scan the TV QR code
      from an iPhone and an Android **on cellular**; confirm the "Continue" page appears once, then
      submit and approve a drawing.
- [ ] Tap **Save to my phone** on both phones; check the card lands in Photos/Gallery.
- [ ] Stop and restart the Dev Tunnel; confirm the URL is the same and phones reconnect.
- [ ] Switch to the quick tunnel backup; paste its URL; confirm the QR code updates and the full flow
      works.
- [ ] Switch the laptop to a phone hotspot; confirm the Dev Tunnel still works.
- [ ] Run the load test through the tunnel; leave `/tank` running for a few hours (soak test).
- [ ] Seed 10–15 creatures drawn by students and approve them.

### Wednesday 10/28: before doors open

- [ ] Laptop plugged in; sleep off; notifications and Windows Update paused.
- [ ] `git pull`, then start the server: `dotnet run --project src/CreatureLab --launch-profile event`.
- [ ] Confirm the console shows the PIN you expect and the right data folder.
- [ ] Create the Dev Tunnel access token (`devtunnel access create creature-lab -a --expiration 12h`),
      then start `devtunnel host creature-lab`; paste the URL into Admin → Settings.
- [ ] Open the TV in kiosk mode; check the QR code, header and panel look right.
- [ ] Scan the QR code from a phone on cellular; release a creature; approve it; watch it arrive.
- [ ] Moderator phones: open `<tunnel>/admin`, enter the PIN, leave sound on.
- [ ] Admin → Settings: snapshots every 5 minutes; tap "Take snapshot now" once to check.
- [ ] Print the sign: "Scan the code on the screen to invent a creature! First name or nickname
      only." (It points at the live screen, not a URL.)

### During the event

- **Inappropriate drawing got through:** Admin → In tank → **Remove**, tap twice. It vanishes
  from the TV and the ticker immediately.
- **QR code stopped working:** restart `devtunnel host creature-lab` (the URL stays the same). If
  the Dev Tunnel keeps failing, switch to the quick tunnel and paste its URL into Admin → Settings.
- **Busy moment, one moderator:** turn on auto-approve (a yellow banner shows it's on); keep
  watching the In tank list. Turn it off when things calm down.
- **Server restarted:** phones and the TV reconnect on their own. The tank, queue, settings and
  PIN all come back from `data/`.

### After the event (6 PM)

- [ ] Admin → Settings → **Download gallery** (all approved drawings, snapshots, `creatures.json`
      and a ready-to-open `index.html` with a time-lapse). It includes artist names, so check it
      before posting anywhere public.
- [ ] Stop the tunnel (Ctrl+C). The public URL stops working.
- [ ] Stop the server and back up the `data/` folder.
- [ ] Optional: `./tools/make-timelapse.ps1` turns the snapshots into an MP4 (needs ffmpeg).

To start a fresh tank, stop the server and move the `data/` folder somewhere else.

## How changes land

`main` is protected: every change arrives as a pull request with one approving review (org
admins can still push an urgent fix directly). Changes written by Claude Code come from the
SpeakingInBits GitHub App (`speakinginbits-bot[bot]`), so a person reviews and approves each one.

GitHub Actions ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) builds the app and runs
`dotnet test` on Windows for every pull request and every push to `main`.

## Project layout

```
DESIGN.md                      engineering design document
docs/walkthrough.gif           the visitor and crew walkthrough at the top of this README
src/CreatureLab/
  Program.cs                   wiring: services, middleware, endpoints, hub
  appsettings.json             settings (see table above); appsettings.Local.json for secrets
  profanity.txt                blocked words for names
  Hubs/TankHub.cs              SignalR hub: display / admin / visitor groups
  Models/Creature.cs           Creature record, statuses, what browsers see
  Services/                    CreatureStore (state + JSON on disk), PngValidator, NameValidator,
                               SubmissionRateLimiter, AdminPinGuard, RuntimeSettings, TankNotifier,
                               StatsBroadcaster, SnapshotStore, GalleryExporter, GalleryPage
  Endpoints/                   REST endpoints (public + admin)
  wwwroot/draw|tank|admin/     the three pages (plain HTML, CSS, JS modules; no build step)
  wwwroot/shared/              theme, SignalR setup, QR drawing, deep-sea painting helpers
  wwwroot/lib/                 vendored SignalR client and qrcode-generator
tests/CreatureLab.Tests/       xUnit unit and API tests
tools/                         LoadTest.cs, make-timelapse.ps1
data/                          runtime data (gitignored): images, creatures.json, snapshots, settings
```
