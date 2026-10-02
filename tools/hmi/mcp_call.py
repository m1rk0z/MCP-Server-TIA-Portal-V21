"""Esegue una sequenza di chiamate su TiaMcpServer (Openness V19).

Uso: python mcp_call.py calls.json [pid]
calls.json = [{"tool": "tia_browse", "args": {...}, "save": "file.json"}, ...]
Si aggancia all'istanza TIA (pid o la prima con progetto), esegue, si sgancia.
"""
import json, os, subprocess, sys, threading, time

EXE = os.environ.get("TIA_MCP_SERVER", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "bin", "V21", "TiaMcpServer.exe"))
calls = json.load(open(sys.argv[1], encoding="utf-8"))
pid = sys.argv[2] if len(sys.argv) > 2 else None  # numero, oppure "none" = non agganciarsi
attach = pid != "none"
pid = int(pid) if pid and attach else None
outdir = os.path.dirname(os.path.abspath(sys.argv[1]))

env = dict(os.environ)
env["TIA_OPENNESS_PATH"] = r"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19"
p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     env=env, text=True, encoding="utf-8", bufsize=1)
threading.Thread(target=lambda: [sys.stderr.write("[srv] " + l) for l in p.stderr], daemon=True).start()
nid = 0


def rpc(method, params=None, notify=False):
    global nid
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None:
        msg["params"] = params
    if not notify:
        nid += 1
        msg["id"] = nid
    p.stdin.write(json.dumps(msg) + "\n")
    p.stdin.flush()
    if notify:
        return None
    while True:
        line = p.stdout.readline()
        if not line:
            raise SystemExit("server chiuso")
        r = json.loads(line)
        if r.get("id") == nid:
            return r


def call(name, args=None, save=None, maxlen=6000):
    t = time.time()
    r = rpc("tools/call", {"name": name, "arguments": args or {}})
    res = r.get("result") or {}
    text = "".join(c.get("text", "") for c in res.get("content", []))
    try:
        data = json.loads(text)
    except Exception:
        data = text or r.get("error")
    if save:
        with open(os.path.join(outdir, save), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"== {name} {json.dumps(args or {}, ensure_ascii=False)} ({time.time() - t:.1f}s) err={res.get('isError')}\n"
          f"{json.dumps(data, ensure_ascii=False)[:maxlen]}", flush=True)
    return data


rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "claude", "version": "1"}})
rpc("notifications/initialized", notify=True)
if attach:
    call("tia_attach", {"pid": pid} if pid else {}, maxlen=400)
def failed(data):
    """Esito negativo di un tool: errore esplicito, import interrotto o compilazione con errori."""
    if not isinstance(data, dict):
        return True
    if data.get("stopped_on_first_error") or data.get("failed"):
        return True
    if isinstance(data.get("errors"), list) and data["errors"]:
        return True
    if isinstance(data.get("errors"), int) and data["errors"] > 0:
        return True
    return False


try:
    for c in calls:
        data = call(c["tool"], c.get("args"), c.get("save"), c.get("maxlen", 6000))
        # "stop_on_error": interrompe la sequenza (e quindi non arriva al salvataggio)
        if c.get("stop_on_error") and failed(data):
            print(f"!! STOP: {c['tool']} non riuscito, sequenza interrotta senza salvare", flush=True)
            break
finally:
    call("tia_detach", maxlen=200)
    p.stdin.close()
    p.wait(timeout=60)
