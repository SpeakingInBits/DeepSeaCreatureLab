using System.Text.Json;
using System.Text.Json.Serialization;
using CreatureLab.Options;

namespace CreatureLab.Services;

/// <summary>
/// The self-contained index.html inside the gallery zip. All names are passed in as
/// JSON and put on the page with textContent, so nothing a visitor typed becomes HTML.
/// </summary>
public static class GalleryPage
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Render(IReadOnlyList<GalleryEntry> creatures, IReadOnlyList<string> snapshots, IReadOnlyList<ProgramLink> programs)
    {
        // The default encoder escapes <, >, & and quotes, so this is safe inside <script>.
        var data = JsonSerializer.Serialize(new
        {
            creatures,
            snapshots,
            programs = programs.Select(p => new { p.Label, p.Url }),
        }, JsonOptions);

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Deep Sea Creature Lab · Gallery</title>
            <style>
              :root { --abyss:#020b16; --deep:#041a33; --glow:#46e6ff; --text:#e8f6ff; --muted:#8fb3c9; }
              * { box-sizing: border-box; }
              body { margin:0; font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; color:var(--text);
                     background: linear-gradient(#07305a, var(--deep) 30%, var(--abyss)); background-attachment: fixed; min-height:100vh; }
              header, main, footer { max-width: 1200px; margin: 0 auto; padding: 24px 16px; }
              .badge { display:inline-block; background:var(--glow); color:#02121f; font-weight:700; letter-spacing:.08em;
                       padding:4px 12px; border-radius:999px; font-size:13px; }
              h1 { font-size: clamp(28px, 5vw, 48px); margin: 10px 0 4px; }
              h2 { margin: 32px 0 12px; }
              .sub { color: var(--muted); margin: 0; }
              .player { background: rgba(0,0,0,.35); border:1px solid rgba(140,220,255,.18); border-radius: 16px; padding: 12px; }
              .player img { width:100%; display:block; border-radius: 10px; background:#000; }
              .controls { display:flex; gap:12px; align-items:center; margin-top:10px; flex-wrap: wrap; }
              .controls input[type=range] { flex:1; min-width: 160px; }
              button { font: inherit; background: var(--glow); color:#02121f; border:0; border-radius: 999px; padding: 8px 18px; font-weight:700; cursor:pointer; }
              .grid { display:grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 16px; }
              figure { margin:0; background: rgba(255,255,255,.05); border:1px solid rgba(140,220,255,.15); border-radius: 14px; overflow:hidden; }
              figure .art { aspect-ratio: 1; display:grid; place-items:center; padding: 12px;
                            background: radial-gradient(circle at 50% 40%, #0b4a7a, #041a33 70%); }
              figure img { max-width:100%; max-height:100%; filter: drop-shadow(0 0 8px rgba(120,220,255,.45)); }
              figcaption { padding: 10px 12px; }
              figcaption strong { display:block; overflow-wrap:anywhere; }
              figcaption span { color: var(--muted); font-size: 14px; }
              footer { color: var(--muted); border-top: 1px solid rgba(140,220,255,.15); }
              footer a { color: var(--glow); }
              .empty { color: var(--muted); }
            </style>
            </head>
            <body>
            <header>
              <span class="badge">COMPUTER PROGRAMMING</span>
              <h1>Deep Sea Creature Lab</h1>
              <p class="sub" id="summary"></p>
            </header>
            <main>
              <section id="timelapse" hidden>
                <h2>Time-lapse</h2>
                <div class="player">
                  <img id="frame" alt="Snapshot of the tank">
                  <div class="controls">
                    <button id="play" type="button">Pause</button>
                    <input id="scrub" type="range" min="0" value="0" aria-label="Snapshot">
                    <span id="frame-label"></span>
                  </div>
                </div>
              </section>
              <section>
                <h2>Creatures</h2>
                <div class="grid" id="grid"></div>
              </section>
            </main>
            <footer>
              <p>Deep Sea Creature Lab · Computer Programming · Program Expo 2026</p>
              <p>Like this? Learn how to build it:</p>
              <ul id="programs"></ul>
            </footer>
            <script id="data" type="application/json">{{data}}</script>
            <script>
              const data = JSON.parse(document.getElementById("data").textContent);
              const fmt = new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" });

              document.getElementById("summary").textContent =
                `${data.creatures.length} creatures invented by visitors at the Program Expo.`;

              const grid = document.getElementById("grid");
              if (data.creatures.length === 0) {
                grid.innerHTML = '<p class="empty">No creatures yet.</p>';
              }
              for (const c of data.creatures) {
                const fig = document.createElement("figure");
                const art = document.createElement("div");
                art.className = "art";
                const img = document.createElement("img");
                img.src = c.file;
                img.alt = c.creatureName;
                img.loading = "lazy";
                art.append(img);
                const cap = document.createElement("figcaption");
                const name = document.createElement("strong");
                name.textContent = c.creatureName;
                const by = document.createElement("span");
                by.textContent = `by ${c.artistDisplay} · ${fmt.format(new Date(c.createdAt))}`;
                cap.append(name, by);
                fig.append(art, cap);
                grid.append(fig);
              }

              const list = document.getElementById("programs");
              for (const p of data.programs) {
                if (!/^https?:\/\//i.test(p.url)) continue;
                const li = document.createElement("li");
                const a = document.createElement("a");
                a.href = p.url;
                a.textContent = p.label;
                a.target = "_blank";
                a.rel = "noopener";
                li.append(a);
                list.append(li);
              }

              if (data.snapshots.length > 0) {
                document.getElementById("timelapse").hidden = false;
                const frame = document.getElementById("frame");
                const scrub = document.getElementById("scrub");
                const label = document.getElementById("frame-label");
                const play = document.getElementById("play");
                scrub.max = data.snapshots.length - 1;
                let index = 0, timer = null;

                const show = (i) => {
                  index = i;
                  scrub.value = i;
                  frame.src = data.snapshots[i];
                  const m = data.snapshots[i].match(/(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})/);
                  label.textContent = m ? `${m[4]}:${m[5]}` : `${i + 1} / ${data.snapshots.length}`;
                };
                const start = () => { timer = setInterval(() => show((index + 1) % data.snapshots.length), 700); play.textContent = "Pause"; };
                const stop = () => { clearInterval(timer); timer = null; play.textContent = "Play"; };

                play.addEventListener("click", () => (timer ? stop() : start()));
                scrub.addEventListener("input", () => { stop(); show(Number(scrub.value)); });
                show(0);
                start();
              }
            </script>
            </body>
            </html>
            """;
    }
}
