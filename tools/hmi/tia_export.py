"""Client MCP minimale: avvia TiaMcpServer (Openness V19), si aggancia a TIA e fa l'export HMI."""
import json, os, subprocess, sys, threading, time

EXE = os.environ.get("TIA_MCP_SERVER", os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "bin", "V19", "TiaMcpServer.exe"))
OUT = os.environ.get("TIA_EXPORT_OUT", os.path.abspath("HMI_EXPORT"))
PID = int(sys.argv[1]) if len(sys.argv) > 1 else None

env = dict(os.environ)
env["TIA_OPENNESS_PATH"] = r"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19"
os.makedirs(OUT, exist_ok=True)
log = open(os.path.join(OUT, "_export_log.jsonl"), "w", encoding="utf-8")

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


def call(name, args=None, save=None):
    t = time.time()
    r = rpc("tools/call", {"name": name, "arguments": args or {}})
    res = r.get("result") or {}
    text = "".join(c.get("text", "") for c in res.get("content", []))
    try:
        data = json.loads(text)
    except Exception:
        data = text or r.get("error")
    log.write(json.dumps({"tool": name, "args": args, "isError": res.get("isError"), "sec": round(time.time() - t, 1), "result": data}, ensure_ascii=False) + "\n")
    log.flush()
    if save:
        with open(os.path.join(OUT, save), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=2)
    short = json.dumps(data, ensure_ascii=False)
    print(f"== {name} ({time.time() - t:.1f}s) err={res.get('isError')} :: {short[:1500]}", flush=True)
    return data


rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "claude-export", "version": "1"}})
rpc("notifications/initialized", notify=True)

call("tia_session", save="00_session.json")
call("tia_instances", save="01_instances.json")
call("tia_attach", {"pid": PID} if PID else {}, save="02_attach.json")
call("tia_devices", save="03_devices.json")
call("hmi_panels", save="04_panels.json")
call("hmi_info", save="05_hmi_info.json")
call("hmi_screens", save="06_screens.json")
call("hmi_tag_tables", save="07_tag_tables.json")
call("hmi_connections", save="08_connections.json")
call("hmi_text_lists", save="09_text_lists.json")
call("plc_list", save="10_plc_list.json")
call("hmi_export_screens", {"out_dir": os.path.join(OUT, "screens"), "with_defaults": True}, save="11_export_screens.json")
call("hmi_export_tags", {"out_dir": os.path.join(OUT, "tags")}, save="12_export_tags.json")
call("hmi_export_text_lists", {"out_dir": os.path.join(OUT, "textlists")}, save="13_export_textlists.json")
call("hmi_tags", {"limit": 0, "details": True}, save="14_hmi_tags.json")
call("tia_detach")
p.stdin.close()
p.wait(timeout=60)
print("FINE")
