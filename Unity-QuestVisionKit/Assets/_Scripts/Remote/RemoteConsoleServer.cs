using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Experimenter web console (PoC): a tiny embedded HTTP server so a laptop
/// browser can monitor and operate the session. **Complement, never a
/// requirement** — every action maps 1:1 onto the same public methods the
/// in-headset chords call; the headset+controllers flow is fully
/// self-sufficient without this component.
///
/// OFF by default (<see cref="startServerOnEnable"/>). Zero packages: plain
/// <see cref="HttpListener"/>. Connect from a laptop via
/// `adb forward tcp:8787 tcp:8787` then http://localhost:8787/ (works on
/// locked-down networks), or the headset's LAN IP.
///
/// Endpoints:
///   GET  /            the dashboard page (self-contained HTML)
///   GET  /status      JSON snapshot (cached on the main thread — no cross-thread Unity calls)
///   POST /action/{startTrials|pause|resume|redo|cyclePreset|recapture|place|toggleDiagnostics}
///                     enqueued to the main thread; responds 202 immediately
///   POST /participant body = ID; written to participant.txt (applies NEXT launch —
///                     the current session's CSV is already open)
///
/// No auth: intended for a USB adb-forward or lab-network PoC only.
/// </summary>
[DisallowMultipleComponent]
public sealed class RemoteConsoleServer : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("Start listening on enable. OFF by default — the console is optional support tooling.")]
    [SerializeField] private bool startServerOnEnable = false;
    [SerializeField] private int port = 8787;

    [Header("Wiring (auto-resolved if empty)")]
    [SerializeField] private SessionFlowController flow;
    [SerializeField] private ObstaclePlacementController placement;
    [SerializeField] private TrialSequencer sequencer;
    [SerializeField] private SessionHUD hud;

    private HttpListener _listener;
    private Thread _thread;
    private volatile bool _running;
    private volatile string _statusJson = "{}";
    private readonly ConcurrentQueue<Action> _mainThread = new();

    public bool IsRunning => _running;
    public int Port => port;

    private void Awake()
    {
        if (!flow) flow = FindAnyObjectByType<SessionFlowController>();
        if (!placement) placement = FindAnyObjectByType<ObstaclePlacementController>();
        if (!sequencer) sequencer = FindAnyObjectByType<TrialSequencer>();
        if (!hud) hud = FindAnyObjectByType<SessionHUD>();
    }

    private void OnEnable()
    {
        if (startServerOnEnable) StartServer();
    }

    private void OnDisable() => StopServer();

    /// <summary>Start listening (idempotent).</summary>
    public void StartServer()
    {
        if (_running) return;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
            _listener.Start();
        }
        catch (Exception e)
        {
            // http://*: needs elevated ACL on some Windows setups — fall back to localhost.
            Debug.LogWarning($"[RemoteConsole] Wildcard bind failed ({e.Message}); falling back to localhost only.");
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }
            catch (Exception e2)
            {
                Debug.LogError($"[RemoteConsole] Could not start: {e2.Message}");
                _listener = null;
                return;
            }
        }

        _running = true;
        _thread = new Thread(ListenLoop) { IsBackground = true, Name = "RemoteConsole" };
        _thread.Start();
        Debug.Log($"[RemoteConsole] Listening on port {port}. adb forward tcp:{port} tcp:{port}");
    }

    /// <summary>Stop listening (idempotent).</summary>
    public void StopServer()
    {
        if (!_running) return;
        _running = false;
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        try { _thread?.Join(500); } catch { }
        _listener = null;
    }

    private void Update()
    {
        while (_mainThread.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Debug.LogWarning($"[RemoteConsole] Action failed: {e.Message}"); }
        }
        if (_running) _statusJson = BuildStatusJson();
    }

    // ---- request handling (listener thread; must not touch Unity APIs) ----

    private void ListenLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { break; }   // listener stopped

            try { Handle(ctx); }
            catch (Exception e)
            {
                try { Respond(ctx, 500, "text/plain", e.Message); } catch { }
            }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
        string method = ctx.Request.HttpMethod;

        if (method == "GET" && (path == "" || path == "/"))
        {
            Respond(ctx, 200, "text/html", DashboardHtml);
        }
        else if (method == "GET" && path == "/status")
        {
            Respond(ctx, 200, "application/json", _statusJson);
        }
        else if (method == "POST" && path.StartsWith("/action/"))
        {
            string action = path.Substring("/action/".Length);
            bool known = EnqueueAction(action);
            Respond(ctx, known ? 202 : 404, "application/json",
                known ? "{\"queued\":\"" + action + "\"}" : "{\"error\":\"unknown action\"}");
        }
        else if (method == "POST" && path == "/participant")
        {
            string body;
            using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = reader.ReadToEnd().Trim();
            if (body.Length == 0 || body.Length > 64)
            {
                Respond(ctx, 400, "application/json", "{\"error\":\"bad id\"}");
                return;
            }
            _mainThread.Enqueue(() =>
            {
                var file = System.IO.Path.Combine(Application.persistentDataPath, "participant.txt");
                System.IO.File.WriteAllText(file, body + "\n");
                hud?.ShowTransient($"Participant ID '{body}' saved — applies next launch", 4f);
            });
            Respond(ctx, 202, "application/json", "{\"saved\":true,\"applies\":\"next launch\"}");
        }
        else
        {
            Respond(ctx, 404, "text/plain", "not found");
        }
    }

    private bool EnqueueAction(string action)
    {
        switch (action)
        {
            case "startTrials": _mainThread.Enqueue(() => flow?.StartTrials()); return true;
            case "pause": _mainThread.Enqueue(() => flow?.Pause()); return true;
            case "resume": _mainThread.Enqueue(() => flow?.Resume()); return true;
            case "redo": _mainThread.Enqueue(() => flow?.RedoTrial()); return true;
            case "cyclePreset": _mainThread.Enqueue(() => placement?.CyclePreset()); return true;
            case "recapture": _mainThread.Enqueue(() => placement?.Recapture()); return true;
            case "place": _mainThread.Enqueue(() => placement?.CapturePlacement()); return true;
            case "toggleDiagnostics": _mainThread.Enqueue(() => hud?.ToggleDiagnostics()); return true;
            default: return false;
        }
    }

    private static void Respond(HttpListenerContext ctx, int code, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = contentType + "; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    // ---- status snapshot (main thread) ----

    private string BuildStatusJson()
    {
        var sb = new StringBuilder(512);
        sb.Append('{');
        sb.Append("\"phase\":\"").Append(flow != null ? flow.Phase.ToString() : "?").Append("\",");
        sb.Append("\"paused\":").Append(flow != null && flow.IsPaused ? "true" : "false").Append(',');
        sb.Append("\"trial\":").Append(sequencer != null ? sequencer.CurrentTrialIndex : -1).Append(',');
        if (placement != null)
        {
            sb.Append("\"placed\":").Append(placement.IsPlaced ? "true" : "false").Append(',');
            sb.Append("\"preset\":\"").Append(Escape(placement.CurrentPresetName)).Append("\",");
            sb.Append("\"solver\":\"").Append(placement.Solver).Append("\",");
            sb.Append("\"policy\":\"").Append(placement.Policy).Append("\",");
            sb.Append("\"variant\":\"").Append(placement.Variant).Append("\",");
            sb.Append("\"anchor\":\"").Append(placement.AnchorStatus).Append("\",");
            float tagAge = placement.SecondsSinceLastTag;
            sb.Append("\"tagAgeS\":").Append(float.IsInfinity(tagAge) ? "-1" : tagAge.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"lastCorrectionMm\":").Append(placement.LastCorrectionMm.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
        }
        if (SessionLogger.Instance != null)
        {
            sb.Append("\"logRunning\":").Append(SessionLogger.Instance.IsRunning ? "true" : "false").Append(',');
            sb.Append("\"logRows\":").Append(SessionLogger.Instance.WrittenCount).Append(',');
            sb.Append("\"participant\":\"").Append(Escape(SessionLogger.Instance.ParticipantId)).Append("\",");
        }
        sb.Append("\"time\":").Append(Time.realtimeSinceStartup.ToString("F1", CultureInfo.InvariantCulture));
        sb.Append('}');
        return sb.ToString();
    }

    private static string Escape(string s)
        => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---- dashboard (self-contained; polls /status once a second) ----

    private const string DashboardHtml = @"<!DOCTYPE html>
<html><head><meta charset='utf-8'><title>Session Console</title>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<style>
 body{font-family:system-ui,sans-serif;background:#111;color:#eee;margin:0;padding:1.2rem;max-width:640px}
 h1{font-size:1.1rem;color:#00E5FF;margin:0 0 1rem}
 .phase{font-size:2rem;font-weight:700;margin:.2rem 0}
 .grid{display:grid;grid-template-columns:1fr 1fr;gap:.4rem .8rem;margin:1rem 0;font-size:.95rem}
 .grid b{color:#9ad}
 button{background:#123a44;color:#00E5FF;border:1px solid #00E5FF;border-radius:8px;
        padding:.8rem 1rem;margin:.25rem;font-size:1rem;cursor:pointer}
 button:active{background:#00E5FF;color:#111}
 button.warn{border-color:#FF2FB9;color:#FF2FB9}
 #err{color:#FF2FB9;min-height:1.2em}
 input{background:#222;color:#eee;border:1px solid #555;border-radius:6px;padding:.5rem}
</style></head><body>
<h1>Obstacle Session Console</h1>
<div class='phase' id='phase'>—</div>
<div class='grid' id='grid'></div>
<div>
 <button onclick=""act('startTrials')"">Start trials</button>
 <button onclick=""act('pause')"">Pause</button>
 <button onclick=""act('resume')"">Resume</button>
 <button onclick=""act('redo')"">Redo trial</button>
 <button onclick=""act('cyclePreset')"">Cycle condition</button>
 <button class='warn' onclick=""act('recapture')"">Re-place</button>
 <button onclick=""act('toggleDiagnostics')"">Diagnostics</button>
</div>
<div style='margin-top:1rem'>
 <input id='pid' placeholder='participant ID (next launch)'>
 <button onclick='setPid()'>Save</button>
</div>
<div id='err'></div>
<script>
async function act(a){try{await fetch('/action/'+a,{method:'POST'});}catch(e){err(e);}}
async function setPid(){const v=document.getElementById('pid').value.trim();
 if(!v)return; try{await fetch('/participant',{method:'POST',body:v});}catch(e){err(e);}}
function err(e){document.getElementById('err').textContent=e;}
async function poll(){
 try{
  const r=await fetch('/status'); const s=await r.json();
  document.getElementById('phase').textContent=s.phase+(s.paused?' (paused)':'');
  const rows=[['Trial',s.trial],['Preset',s.preset],['Solver',s.solver],['Policy',s.policy],
   ['Variant',s.variant],['Placed',s.placed],['Anchor',s.anchor],
   ['Tag seen',s.tagAgeS<0?'never':s.tagAgeS+'s ago'],['Last corr',s.lastCorrectionMm+' mm'],
   ['Log rows',s.logRows],['Participant',s.participant]];
  document.getElementById('grid').innerHTML=rows.map(r=>'<div><b>'+r[0]+'</b></div><div>'+r[1]+'</div>').join('');
  err('');
 }catch(e){err('disconnected');}
 setTimeout(poll,1000);
}
poll();
</script></body></html>";
}
