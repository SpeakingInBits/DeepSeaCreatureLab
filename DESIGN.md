# Engineering Design Document: Deep Sea Creature Lab

> **Status:** Draft v0.7. Items marked **[TBD]** need a decision before or during the build.
> **Owner:** Joe (Computer Programming program)
> **Event:** College Program Expo, **Wednesday, October 28, 2026, 3:00–6:00 PM**
> **Build window:** Fri Oct 23 – Tue Oct 27 (about 5 days), part-time, alongside normal duties. Built by Joe with Claude Code.

---

## 1. Summary

A shared, live deep-sea scene for the Program Expo, invented by visitors (any creature, not just fish). Visitors scan a QR code that is always on the TV, draw a creature on their phone, name it, sign it with their own first name or nickname, and release it. After a staff member approves it, the creature appears on the big screen and swims around with everyone else's drawings. A side panel shows the system working in real time (connected phones, messages flowing from phone to server to screen), which gives booth staff a natural way to explain what students learn in the program.

No app install, no accounts, no hardware. Everything runs from one laptop at the booth, exposed to the internet through a tunnel.

## 2. Goals

1. **Attention-grabbing from across the room.** The tank looks alive even when nobody is interacting.
2. **Zero-friction participation.** Scan, draw, release in under 60 seconds on any modern phone.
3. **Safe on a big screen.** Nothing appears on the TV without staff approval.
4. **Shows off the program.** Visitors can see that their phone is talking to a real server the students built.
5. **Buildable in about five days** by one developer working part-time with Claude Code.
6. **Leaves an artifact.** Every approved drawing is saved for a gallery or time-lapse after the event.

## 3. Non-goals

- User accounts, logins, or collecting personal data.
- Native mobile apps.
- Cloud hosting or paid infrastructure.
- Hardware integration (LED matrix, ESP32, etc.). Possible future extension, out of scope for this build.
- Multi-room or multi-tank support.
- AI classification or automatic moderation of drawings.

## 4. Event context and constraints

| Item | Value |
|---|---|
| Display | HD TV (1920×1080) **or** a wide-screen monitor (e.g. 2560×1080 / 3440×1440), plugged into the server laptop. Browser in fullscreen/kiosk mode at `http://localhost:5000/tank`. Layout must adapt to 16:9 and ultrawide (21:9). |
| Host machine | Booth laptop running the server, **[TBD: Windows / macOS / Linux]** |
| Network | Visitors are NOT on the same network as the laptop. Public access via tunnel (see §10). |
| Visitor devices | Personal phones, iOS Safari and Android Chrome, on cellular or campus Wi-Fi |
| Expected traffic | 0–10 visitors at a time over a 3-hour event. Design target: 150 drawings total, 25 concurrent phones (headroom over expected). |
| Staff | **[TBD: number of staff/student helpers at the booth]** |
| Program branding | "Computer Programming" shown prominently on screen. **[TBD: colors and logo, if any]** |

## 5. Users and roles

| Role | Device | Page | What they do |
|---|---|---|---|
| **Visitor** | Their phone | `/draw` | Draw a creature, pick a habitat, name it, sign it, release it, see its status |
| **Moderator** | Staff phone or laptop | `/admin` | Approve or reject pending drawings; remove creatures already in the tank |
| **Display** | Booth TV | `/tank` | Shows the animated tank, QR code, and live system panel |

## 6. User flows

### 6.1 Visitor
1. Sees the TV, scans the QR code shown in the corner.
2. `/draw` opens. A canvas shows a faint fish outline (head on the right) as a guide.
3. Picks a habitat: **Swimmer**, **Bottom crawler**, or **Floater** (see §8.3).
4. Draws with a few colors and two brush sizes. Has Undo and Clear.
5. Enters two short fields:
   - **Creature name** (required, max 20 characters), e.g. "Gloop the Destroyer".
   - **Artist name** (optional, max 20 characters, placeholder "First name or nickname"). Blank shows as "a mystery diver".
   - Both values are remembered on the device (`localStorage`, wrapped in try/catch) so a returning visitor's artist name is pre-filled.
6. Taps **Release**. The page shows "Waiting for approval…".
7. On approval, the page shows "Your creature is in the tank! Look at the TV." On rejection, it shows a friendly message and invites them to draw again.
8. Rate limit: one submission per device every **30** seconds (configurable).
9. At any point after tapping **Release**, a **Save to my phone** button lets the visitor keep a copy of their creature (see §8.3.1).

### 6.2 Moderator
1. Opens `/admin` and enters the PIN.
2. Sees a queue of pending drawings (image, creature name, artist name, habitat, time submitted), oldest first.
3. Taps **Approve** or **Reject** on each. Approved creatures enter the tank immediately.
4. Can view creatures currently in the tank and tap **Remove** on any of them.
5. Can toggle **auto-approve** on or off (default OFF), e.g. during slow periods. Remove still works while auto-approve is on.

### 6.3 Display
1. TV opens `/tank` in a fullscreen browser and stays there all day.
2. Shows the tank, a persistent QR code with a short "Scan to invent a creature!" call to action, and the live system panel.
3. When a creature is approved, it enters with a short arrival effect and its name tag ("{creature} by {artist}") fades in.
4. On a page reload or server restart, the tank repopulates from saved state.

## 7. Architecture

```
 Visitor phones ──HTTPS/WSS──┐
                             │
 Moderator device ──HTTPS/WSS┤──► Tunnel (cloudflared) ──► Booth laptop
                             │                              ┌─────────────────────────┐
                             │                              │ ASP.NET Core app        │
 TV browser ─────────────────┘ (local or via tunnel)        │  • Static pages         │
                                                            │  • REST endpoints       │
                                                            │  • SignalR hub          │
                                                            │  • In-memory state      │
                                                            │  • Disk persistence     │
                                                            └─────────────────────────┘
```

- **Server:** ASP.NET Core, **.NET 10**, single project.
- **Real-time:** SignalR over WebSockets. One hub with groups for `display`, `admin`, and individual visitors.
- **Front end:** Plain HTML, CSS, and JavaScript served from `wwwroot`. No front-end framework or build step. SignalR JS client and a QR code library loaded from a CDN or vendored into `wwwroot/lib`.
- **Rendering:** Canvas 2D on the tank page (sufficient for HD and ~30 sprites; keeps the code readable).
- **State:** In-memory, persisted to a `data/` folder (images as PNG files, metadata as JSON).

### 7.1 Repository layout (proposed)

```
/
├── DESIGN.md                  (this document)
├── README.md                  (how to run, tunnel setup, event-day checklist)
├── src/CreatureLab/
│   ├── Program.cs
│   ├── appsettings.json
│   ├── Hubs/TankHub.cs
│   ├── Models/Creature.cs
│   ├── Services/CreatureStore.cs
│   ├── Services/RateLimiter.cs
│   ├── Endpoints/CreatureEndpoints.cs
│   └── wwwroot/
│       ├── draw/index.html, draw.js, draw.css
│       ├── tank/index.html, tank.js, tank.css
│       ├── admin/index.html, admin.js, admin.css
│       └── lib/               (vendored JS libraries)
├── tests/CreatureLab.Tests/
└── data/                      (gitignored; runtime images and metadata)
```

## 8. Detailed design

### 8.1 Data model

```csharp
enum Habitat { Swimmer, BottomCrawler, Floater }
enum CreatureStatus { Pending, Approved, Rejected, Removed, Retired }

record Creature(
    Guid Id,
    string CreatureName,    // required, trimmed, max 20 chars
    string? ArtistName,     // optional, trimmed, max 20 chars; null shows as "a mystery diver"
    Habitat Habitat,
    string ImageFile,       // e.g. "data/images/{Id}.png"
    CreatureStatus Status,
    string ClientId,        // random ID stored in the visitor's localStorage; not personal data
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);
```

`Retired` means approved but pushed out of the tank by the population cap (§8.4). Retired creatures stay in the saved gallery.

### 8.2 Server interface

**REST**

| Method | Path | Who | Purpose |
|---|---|---|---|
| `POST` | `/api/creatures` | Visitor | Submit a drawing (`multipart/form-data`: `image`, `creatureName`, `artistName` (optional), `habitat`, `clientId`). Returns `{ id, status }`. |
| `GET` | `/api/creatures/{id}` | Visitor | Poll status (fallback if SignalR disconnects). |
| `GET` | `/api/creatures/{id}/image` | All | Serve the PNG. |
| `GET` | `/api/tank` | Display | Current approved creatures for initial load. |
| `GET` | `/api/admin/pending` | Moderator | Pending queue. Requires PIN. |
| `POST` | `/api/admin/creatures/{id}/approve` | Moderator | Requires PIN. |
| `POST` | `/api/admin/creatures/{id}/reject` | Moderator | Requires PIN. |
| `POST` | `/api/admin/creatures/{id}/remove` | Moderator | Requires PIN. |
| `GET` | `/api/config` | Display | Returns `publicBaseUrl` for the QR code. |
| `POST` | `/api/snapshots` | Display | Upload a timed snapshot PNG (localhost or PIN). |
| `GET` | `/api/admin/export` | Moderator | Download the gallery zip (§8.8). Requires PIN. |
| `POST` | `/api/admin/settings` | Moderator | Update `publicBaseUrl`, auto-approve, snapshot interval at runtime. Requires PIN. |

**SignalR hub (`/hubs/tank`) server-to-client events**

| Event | Sent to | Payload |
|---|---|---|
| `CreatureSubmitted` | `admin` | Creature summary |
| `CreatureApproved` | `display`, `admin`, owning visitor | Creature summary + image URL |
| `CreatureRejected` | `admin`, owning visitor | `{ id }` |
| `CreatureRemoved` | `display`, `admin` | `{ id }` |
| `CreatureRetired` | `display` | `{ id }` |
| `Stats` | `display` | `{ connectedPhones, submissionsTotal, approvedTotal, messagesPerMinute }`, every ~2 s |
| `Activity` | `display` | `{ kind, at }` for the live system panel animation |
| `TakeSnapshot` | `display` | `{}` — capture now (admin button) |
| `SettingsChanged` | `display`, `admin` | `{ publicBaseUrl, autoApprove, snapshotIntervalMinutes }` |

**Client-to-server hub methods:** `JoinDisplay()`, `JoinAdmin(pin)`, `JoinVisitor(clientId)`.

### 8.3 Draw page (`/draw`)

- Canvas sized to the phone width, square-ish, internal resolution **512×512**, transparent background.
- Faint fish outline drawn on a separate guide layer that is NOT included in the submitted image.
- Pointer Events for drawing (works with touch and mouse). Prevent page scroll and pinch-zoom while drawing.
- Palette: 8 bright colors plus black. Two brush sizes. Undo (stroke history), Clear.
- Habitat picker as three large buttons with icons.
- Creature name field (required) and artist name field (optional), each max 20 characters, with a live character counter. Release stays disabled until there is a drawing and a creature name.
- Before upload: crop to the drawing's bounding box plus padding, scale down so the longest side is ≤ 512 px, export PNG. Block empty submissions.
- After submission: subscribe to status updates via SignalR, with polling fallback every 5 s.
- `clientId`: random GUID generated on first visit and kept in `localStorage` (wrapped in try/catch; fall back to an in-memory ID).

#### 8.3.1 Save to phone

- Shown after **Release** (whether pending, approved, or rejected; it's the visitor's own drawing). Generated entirely in the browser from the drawing, so it works even if the server or tunnel hiccups.
- Produces a **keepsake card**, not the raw transparent PNG (transparent images look odd in phone galleries): 1080×1350 PNG with the deep-sea background, the creature centered, its name in large type with "drawn by {artist}" underneath, and a footer: "Deep Sea Creature Lab · Computer Programming · Program Expo 2026".
- The card's footer also carries the two program links (§8.3.2) as short printed URLs, each with a small QR code, so anyone who sees the card later (on the visitor's phone or a social post) can reach the program pages. Links in an image aren't clickable, which is why the QR codes are there.
- **Saving, by platform:**
  - Primary: Web Share API with a file (`navigator.canShare({ files })` → `navigator.share({ files: [card] })`). On iOS this opens the share sheet with **Save Image** (goes straight to Photos); on Android it offers Photos/Gallery and other apps. Requires HTTPS (the tunnel provides it) and must be called directly inside the button tap handler.
  - Fallback: if file sharing isn't supported, open the card image full-screen with the hint "Press and hold the image, then tap Save." (`<a download>` alone is unreliable on iOS, where it saves to Files rather than Photos.)
- Pre-render the card when Release is tapped so the share call happens synchronously in the tap (some browsers reject `share()` after an `await`).
- Test on both iPhone and Android over the tunnel on Tuesday.


#### 8.3.2 Program links

Two degree links, stored in `appsettings.json` under `Programs` (label + URL) so they can be changed without code edits:

| Label | URL |
|---|---|
| Computer Programming (associate degree; CPTC lists it as an AAT) | https://www.cptc.edu/programs/computer-programming |
| B.S. in Computer Science and Cyber-Physical Software Engineering | https://www.cptc.edu/programs/BSCompSci |

Where they appear:
- **Draw page, after Release:** a "Like this? Learn how to build it." section with two large buttons, one per program. Links open in a new tab (`target="_blank" rel="noopener"`) so the visitor doesn't lose their status page.
- **Keepsake card:** printed short URLs plus a small QR code for each (§8.3.1).
- **Gallery export `index.html`:** both links in the footer.
- **Tank page:** a single line under the system panel with both program names (no URLs; the QR code there stays dedicated to "invent a creature" to avoid confusing visitors).

### 8.4 Tank page (`/tank`)

**Layout**
- Main area: deep-sea scene (dark blue-to-black gradient, faint light rays from above, seafloor strip at the bottom, drifting bubbles and marine snow, optional glowing bioluminescent accents).
- Title on screen: "Deep Sea Creature Lab".
- Corner: QR code generated in the browser from `publicBaseUrl + "/draw"`, plus call to action text. Regenerates if the URL in config changes, so a tunnel restart never needs a reprinted sign.
- Side or bottom strip: live system panel (§8.6).
- Header: "Deep Sea Creature Lab" with "Computer Programming" displayed prominently.
- **Aspect ratio:** the canvas fills the viewport and recomputes bounds on resize. On ultrawide screens the tank stretches horizontally (more swimming room); the QR code and system panel stay anchored to the right edge at a fixed size.

**Population**
- Cap: **30** creatures on screen (configurable). When a new one is approved and the tank is full, the oldest swims off-screen and is marked `Retired`.
- On load, fetch `/api/tank` and spawn creatures at random positions.

**Animation (per creature, per frame)**
- **Swimmer:** moves horizontally at a randomized speed; vertical position follows a slow sine bob; turns around at edges or occasionally at random; sprite flipped horizontally when moving left.
- **Bottom crawler:** walks along the sand line with a small hop or step bob; turns at edges.
- **Floater:** drifts slowly in the upper third with a gentle vertical pulse (slight squash and stretch).
- **Body wave ("tail wiggle"):** render the sprite as N vertical slices (e.g. 16–24). Offset each slice vertically by `amplitude × sin(time × freq + sliceIndex × phase)`, with amplitude increasing toward the tail end (the left side when facing right). Swimmers get the strongest wave, floaters a soft one, crawlers little or none.
- **Arrival effect:** new creature enters from the side with a bubble burst; its full name tag ("{creature} by {artist}") shows for ~5 s, then shrinks to just the creature name.
- **Name tags:** small label under each creature showing the creature name, low opacity, so the tank stays readable. The artist name appears in the arrival tag and in a "Latest arrivals" ticker (last 5, e.g. "Gloop the Destroyer by Sam") in the system panel area.
- Target 60 fps at 1920×1080 (and acceptable on 3440×1440) with 30 creatures. Use `requestAnimationFrame` and pre-loaded `Image` objects.

**Resilience**
- SignalR automatic reconnect. On reconnect, re-fetch `/api/tank` and reconcile.
- Page must keep running all day without memory growth (remove images and objects for retired creatures).

### 8.5 Admin page (`/admin`)

- PIN prompt; PIN set in `appsettings.json` or an environment variable. PIN sent as a header on admin REST calls and as an argument to `JoinAdmin`.
- Pending queue as large cards with big Approve and Reject buttons (usable one-handed on a phone).
- Badge with pending count, and an optional sound or vibration when new drawings arrive.
- "In tank" list with Remove buttons.
- Auto-approve toggle, default OFF. While on, the admin page shows a clear "AUTO-APPROVE ON" banner and the pending queue is bypassed.

### 8.6 Live system panel ("How it works")

Shown on the tank page to support the program pitch:
- Counters: phones connected, creatures drawn today, creatures in the tank.
- Simple animated diagram: **Phone → Server → Screen**, where a dot travels along the arrow each time an `Activity` event arrives.
- One line of text: "Built by Computer Programming with C#, ASP.NET Core, and SignalR."
- Below it, the two program names from `Programs` (see §8.3.2).

**In scope for v1.**

### 8.7 Persistence

- On submit: write `data/images/{id}.png`; append or rewrite `data/creatures.json`.
- On startup: load `creatures.json`; restore approved, non-retired creatures into the tank.
- `data/` is gitignored.
- Gallery export: see §8.8 (in scope for v1).

### 8.8 Gallery export and timed snapshots (v1)

**Timed snapshots**
- Setting `Snapshots:IntervalMinutes` (default **5**; `0` disables). Also adjustable from the admin page at runtime.
- The tank page captures the full display (tank plus header; the QR code and system panel may be included or excluded via `Snapshots:IncludeOverlay`, default `true`) using `canvas.toBlob("image/png")`. To make this simple, draw the header, QR code, and panel onto the same canvas, or onto a separate composite canvas at capture time.
- The blob is uploaded to `POST /api/snapshots` (accepted only from localhost or with the admin PIN). Server saves to `data/snapshots/{yyyyMMdd-HHmmss}.png`.
- Admin page has a **Take snapshot now** button (sends a hub message telling the display to capture immediately).
- All drawings are served same-origin, so the canvas is never tainted and `toBlob` works.

**Gallery export**
- Admin page has a **Download gallery** button → `GET /api/admin/export` (PIN required) returns a zip:
  - `creatures/` — every approved creature PNG, named `{createdAt}-{creatureName}-by-{artistName}.png` (sanitized)
  - `snapshots/` — all timed snapshots, in order (ready for a time-lapse)
  - `creatures.json` — metadata (creature name, artist name, habitat, times; no client IDs)
  - `index.html` — a self-contained gallery page: grid of creatures labeled "{creature} by {artist}", plus a snapshot slideshow that auto-plays as a time-lapse, with both program links in the footer
- Zip built with `System.IO.Compression` on request; no extra dependencies.
- Optional `tools/make-timelapse` script that turns `snapshots/` into an MP4 with ffmpeg, if ffmpeg is installed. Nice-to-have, not required.

## 9. Security, safety, and privacy

- **Moderation:** approval queue by default; nothing reaches the TV without a staff tap.
- **Admin access:** PIN-protected; the PIN is never shown on screen or committed to the repo.
- **Upload validation:** PNG only (check magic bytes, not just content type); max **256 KB**; max **512×512**; decode server-side to confirm it is a valid image. Both name fields trimmed, length-limited, and HTML-encoded on display. Basic profanity word list applied to both name fields, kept in a plain text file the server loads at startup (easy to extend).
- **Rate limiting:** per `clientId` and per IP (as seen through the tunnel's forwarded headers); one submission per **30** s, plus a global cap of pending items (e.g. 100) to protect the moderator.
- **Privacy:** no emails or accounts. The artist name is optional and free text; the phone UI and the sign both say "First name or nickname only." Artist names are included in the gallery export, so treat the export as something to share thoughtfully (e.g. check it before posting publicly).
- **Tunnel exposure:** the public URL is reachable by anyone who has it during the event. Admin endpoints require the PIN. Shut the tunnel down after the event.

## 10. Hosting and networking

The app runs on the booth laptop. Phones reach it through a tunnel: a small client on the laptop opens an outbound connection to a tunnel service, which gives a public HTTPS URL that forwards to `http://localhost:5000`. No campus network changes and no server costs.

### 10.1 Tunnel options

| | **A. Cloudflare quick tunnel** | **B. Cloudflare named tunnel** | **C. Microsoft Dev Tunnels** |
|---|---|---|---|
| Role for this event | **Primary** | Not planned | **Backup** |
| Account needed | None | Cloudflare account + a domain managed in Cloudflare | Microsoft, Entra ID, or GitHub account |
| URL | Random `*.trycloudflare.com`; changes every time the tunnel starts | Permanent, on your domain (e.g. `creatures.example.edu`) | `*.devtunnels.ms`; stays the same across restarts if you create a persistent tunnel |
| Visitor experience | Straight to the page | Straight to the page | Anti-phishing interstitial on first visit; visitor taps **Continue** once |
| Capacity | Up to 200 in-flight requests per tunnel (excess gets HTTP 429). Far above our 0–10 concurrent visitors. | Higher limits; not a concern at this scale | Published limits not verified; ample for this scale |
| Caveats | No uptime guarantee; **no Server-Sent Events** support | Setup takes longer (DNS, tunnel config, credentials file) | Must enable anonymous access; the interstitial adds friction at a booth |
| Start command | `cloudflared tunnel --url http://localhost:5000` | `cloudflared tunnel run <name>` (after one-time setup) | `devtunnel host -p 5000 --allow-anonymous`, or a persistent tunnel (below) |

**Decision:** A as primary, C prepared in advance as backup. B is only worth it if a Cloudflare-managed domain already exists; a stable URL isn't needed because the QR code is generated live.

### 10.2 Option details

**A. Cloudflare quick tunnel (primary)**
- Install `cloudflared` on the booth laptop. No login required.
- Run `cloudflared tunnel --url http://localhost:5000`, copy the printed `https://….trycloudflare.com` URL, and paste it into the admin page's **Public URL** setting. The TV QR code updates immediately.
- The URL dies when `cloudflared` stops. Leave its terminal window open for the whole event.

**B. Cloudflare named tunnel (optional)**
- One-time: `cloudflared tunnel login`, `cloudflared tunnel create creature-lab`, then `cloudflared tunnel route dns creature-lab <hostname>`, and a config file mapping the hostname to `http://localhost:5000`.
- Event day: `cloudflared tunnel run creature-lab`. Set `PublicBaseUrl` once in `appsettings.json`; it never changes.

**C. Microsoft Dev Tunnels (backup)**
- One-time, before event day:
  - `devtunnel user login` (Microsoft or GitHub account)
  - `devtunnel create creature-lab`
  - `devtunnel port create creature-lab -p 5000`
  - `devtunnel access create creature-lab -a --expiration 6h` (anonymous access; expiration can be 1h–30d and is fixed from when it's created, so create it on the morning of the event)
- Event day (if switching): `devtunnel host creature-lab`, then paste its URL into the admin page.
- The persistent URL can be noted in advance, so switching takes under a minute.
- Visitors see a one-time "continue" page. Booth staff should be ready to say "tap Continue, it's our tunnel."
- Visual Studio's built-in tunnel feature is the same service and can be used during development.

### 10.3 App settings that apply to every option

- **Public URL config:** `PublicBaseUrl` in `appsettings.json` or an environment variable, also editable from the admin page at runtime so a new tunnel URL can be applied without a restart. The tank page QR code updates automatically.
- **SignalR transports:** configure the JS client to use **WebSockets, falling back to long polling only** (skip Server-Sent Events, which quick tunnels don't support): `withUrl("/hubs/tank", { transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling })`.
- **Forwarded headers:** enable `UseForwardedHeaders` (trusting `X-Forwarded-For` and `X-Forwarded-Proto`) so request scheme and client IP are correct behind any tunnel. Rate limiting relies on this.
- **Display:** the TV or wide-screen monitor is plugged into the server laptop and opens `http://localhost:5000/tank` directly (no tunnel latency). Snapshot uploads from the display therefore come from localhost (§8.8).
- **Fallback:** phone hotspot for the laptop if venue internet fails. Both tunnels work over a hotspot.

### 10.4 Rehearsal checklist (Tue 10/27)

- [ ] Start the quick tunnel, set the URL, scan the TV QR code from an iPhone and an Android **on cellular**, submit and approve a drawing.
- [ ] Stop and restart the quick tunnel; paste the new URL; confirm the QR code updates and phones reconnect.
- [ ] Switch to the Dev Tunnel backup; confirm the interstitial appears once and the full flow works.
- [ ] Switch the laptop to a phone hotspot; confirm the quick tunnel still works.

## 11. Testing plan

- **Unit tests:** `CreatureStore` (status transitions, population cap and retirement, persistence round-trip), upload validation, rate limiter.
- **Load test:** a script that simulates 50 phones submitting drawings over a few minutes, through the tunnel.
- **Device test:** at least one iPhone (Safari) and one Android (Chrome) on cellular data, through the tunnel.
- **Soak test:** leave `/tank` running for 4+ hours with simulated traffic; watch memory and frame rate.
- **Dress rehearsal:** full setup on the actual TV and laptop the day before, including restarting the tunnel and confirming the QR code updates.

## 12. Build plan (Fri Oct 23 – Wed Oct 28)

| Day | Milestone |
|---|---|
| **Fri 10/23** | Repo, .NET 10 project skeleton, `CreatureStore`, submit endpoint, SignalR hub. Draw page submits a PNG end-to-end. |
| **Sat–Sun 10/24–25** (optional) | Admin page (approve/reject/remove, auto-approve toggle). Tank page shows approved creatures as static sprites. |
| **Mon 10/26** | Animation: swim paths, body wave, flipping, habitats, population cap. Persistence and reconnect handling. QR code from config. |
| **Tue 10/27** | Deep-sea visuals, header, live system panel, timed snapshots, gallery export, validation and rate limiting. Tunnel test on phones over cellular. **Evening: dress rehearsal on the real TV/monitor**; seed 10–15 creatures. |
| **Wed 10/28 AM** | Bug fixes only. Print the QR sign (pointing at the live screen, not a URL). Event 3–6 PM. Download the gallery before shutting down. |

**Cut list if behind (in order):** `tools/make-timelapse`, gallery `index.html` (keep the zip of PNGs), floater habitat, profanity filter on names (moderator covers it), arrival effect polish.

**Ownership:** Joe, building with Claude Code.

## 13. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Venue internet down or slow | Phone hotspot fallback; test bandwidth beforehand |
| Quick tunnel drops or misbehaves | Restart it and paste the new URL, or switch to the prepared Dev Tunnel backup (§10.2 C) |
| Tunnel URL changes on restart | QR generated live from config; runtime URL update on admin page |
| Inappropriate drawings | Approval queue on by default; remove button for anything that slips through |
| Moderator overwhelmed at peak | Global pending cap; per-device rate limit; second moderator device |
| TV browser slows over a long day | Population cap, retired-object cleanup, soak test |
| Phones scroll or zoom while drawing | `touch-action: none` on canvas; viewport meta; test on both platforms |
| Low participation | Seed the tank with 10–15 drawings made by students before doors open; staff invite passersby |

## 14. Future extensions (out of scope)

- Feed creatures (tap a "food" button on the phone; nearby creatures swim toward it).
- Physical output from the Cyber-physical program: an LED matrix or light strip that flashes when a creature arrives.
- Quest 3 mixed-reality view of the tank.
- Post-event web gallery and time-lapse export.
- Reuse as a teaching project (SignalR, file uploads, canvas animation) in .NET courses.

## 15. Open questions

1. Booth laptop OS (Windows, macOS, or Linux)? Affects the `cloudflared` install steps in the README.
2. Program colors or logo to use on screen, if any?
3. How many people will staff the booth? (Decides whether one person can moderate and talk to visitors at once, or auto-approve is needed during busy moments.)
4. Should the on-screen credit line mention Claude Code (e.g. "Built by Computer Programming with C#, ASP.NET Core, SignalR, and Claude Code")?

### Decided
- .NET 10. Canvas 2D rendering. Deep Sea Creature Lab theme with "Computer Programming" on screen.
- Approval queue with auto-approve toggle (default off).
- Live system panel in v1. Gallery export and timed snapshots in v1.
- Cloudflare quick tunnel as primary, Microsoft Dev Tunnel as prepared backup (§10); display runs on the server laptop via localhost.
- Event: Wed Oct 28, 3–6 PM, 0–10 concurrent visitors, HD TV or wide-screen monitor.

---

## Notes for Claude Code

- Build in the order of §12. Get the end-to-end path (draw → submit → approve → appears on tank) working before any animation polish.
- Keep the front end dependency-free except for the SignalR client and a QR code library; vendor both into `wwwroot/lib`.
- Treat every **[TBD]** above as a decision to confirm with the owner, not as a placeholder to invent.
- Include a README with: how to run locally, how to start each tunnel option in §10 (quick tunnel, Dev Tunnel backup, and optional named tunnel), how to set `PublicBaseUrl` and the admin PIN, and an event-day checklist.
