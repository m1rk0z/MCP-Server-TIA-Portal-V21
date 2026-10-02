# TIA Portal on another machine: agent and client

When TIA Portal runs on a different machine from Claude Code — typically a VM —
the server is split in two:

```
PC (Claude Code)                                   VM (TIA Portal V19 / V21)
+------------------------------+   HTTP + token  +----------------------------------+
| Claude Code                  |  -------------> | TiaAgent.exe   (TIA icon in tray)|
|   TiaMcpClient.exe           |   port 8766     |   V19\TiaMcpServer.exe           |
|   --agent http://VM:8766     |  <------------- |   V21\TiaMcpServer.exe           |
|   --version V21              |   replies+files |     -> TIA Portal (Openness)     |
+------------------------------+                 +----------------------------------+
```

- **`TiaMcpClient.exe`** (PC) is an ordinary stdio MCP server for Claude Code. It forwards
  every message to the agent and handles files (below).
- **`TiaAgent.exe`** (VM) runs in the desktop session of the user who runs TIA Portal and
  starts one `TiaMcpServer.exe` per client session, of the requested version.

**One Claude Code session = one agent session = one `TiaMcpServer` process = one `Attach()`.**
The Openness confirmation appears once, on the VM desktop, exactly as when the server runs
locally. That is also why the agent is not a Windows service: it must live in the session
where TIA Portal runs and where the confirmation is shown.

The tools are the same 35 as the local server: the client does not rewrite them.

## Files

Tools that read or write files work on the VM. The client makes that transparent:

| argument | tools | what the client does |
|---|---|---|
| `files`, `dir` | `hmi_import_screens`, `hmi_import_tags`, `hmi_import_text_lists` | a path that exists on the PC is uploaded to the session folder on the VM and replaced |
| `out_dir` | `hmi_export_screens`, `hmi_export_tags`, `hmi_export_text_lists`, `plc_export_blocks` | the export goes to the session folder on the VM, then every file is downloaded into `out_dir` on the PC and the paths in the reply are rewritten |

A path that does not exist on the PC is passed unchanged: it is taken as a VM path (for
example `tia_open_project` on a project stored on the VM). Relative `out_dir` paths are
relative to `%LOCALAPPDATA%\tia-mcp\remote\<host>_<port>\<version>` (or `--workdir`).

The session folder on the VM (`%ProgramData%\TiaAgent\work\<session>`) is deleted when the
session closes.

## Installing the agent (VM)

```
.\build.ps1                      on the VM: servers V19/V21 + agent + client, and bin\TiaAgent.zip
bin\package\TiaAgent\setup.cmd   or: unzip TiaAgent.zip anywhere and run setup.cmd
```

`setup.cmd` checks .NET Framework 4.8, asks for administrator rights and opens a dialog:
port (8766), mode (read-only recommended), allowed client IPs. Unattended:

```
setup.cmd --silent --port 8766 --access-mode read-only --allow 192.168.56.1
```

It installs to `C:\Program Files\TiaAgent` (with `V19\`, `V21\`), stores configuration and
token in `C:\ProgramData\TiaAgent\agent.json` (administrators only may change it), reserves
the URL with `netsh http urlacl`, opens the port in the firewall for the allowed IPs only,
starts the agent at every logon, and adds an entry to *Programs and Features*.

At the end it shows the block to paste on the PC — one MCP server per TIA version.

**read-only** starts every `TiaMcpServer` with `--read-only`: tools that write are refused
whatever the client asks.

## Configuring Claude Code (PC)

Build the client on the PC (`.\build.ps1` builds agent and client even without TIA Portal),
then in `%USERPROFILE%\.claude.json`:

```json
"tia-v21": {
  "type": "stdio",
  "command": "C:\\path\\tia-mcp\\bin\\client\\TiaMcpClient.exe",
  "args": ["--agent", "http://192.168.56.10:8766", "--version", "V21"],
  "env": { "TIA_MCP_AGENT_TOKEN": "<token shown by the agent>" }
},
"tia-v19": { "...": "same, with --version V19" }
```

| option | env | |
|---|---|---|
| `--agent <url>` | `TIA_MCP_AGENT_URL` | without a port, 8766 |
| `--version V19\|V21` | `TIA_MCP_VERSION` | which server to start on the VM |
| `--agent-token <t>` | `TIA_MCP_AGENT_TOKEN` | required; better in `env` |
| `--workdir <dir>` | | local folder for relative `out_dir` |
| `--timeout <min>` | | per call, default 30 (compiles can be long) |

## Agent protocol

Every request carries `Authorization: Bearer <token>`.

| request | |
|---|---|
| `GET /api/health` | machine, versions available, TIA instances, open sessions |
| `POST /api/sessions` `{"version":"V21"}` | starts a server → `{session, work_dir}` |
| `POST /api/sessions/{id}/rpc` | one JSON-RPC message → the server's reply (204 for a notification) |
| `POST /api/sessions/{id}/ping` | the client is alive (sent every minute) |
| `DELETE /api/sessions/{id}` | stops the server, which releases Openness |
| `PUT /api/sessions/{id}/upload?name=` | file into the session folder |
| `GET /api/sessions/{id}/list?dir=` · `/file?path=` | listing / download, confined to the session folder |

Idle sessions are closed after 12 hours (`SessionIdleMinutes` in `agent.json`).

The client pings its session every minute. When the ping stops for 3 minutes
(`KeepAliveSeconds`), the agent closes the session. This happens when the client was
killed without closing stdin, which is how MCP hosts often stop their servers. Without
the ping, every restart of Claude Code would leave a `TiaMcpServer` running on the VM
for 12 hours. Sessions opened by clients older than 1.1.1 are not pinged and keep the
old rule.

## Security

- Random 256-bit token, compared in constant time; 401 on a wrong token, 403 for a client
  not in the allowed list (also enforced by the firewall rule).
- Files confined to the session folder, on the VM and on the PC.
- Plain HTTP: use a private or host-only network between PC and VM, a VPN, or an SSH tunnel
  (`"ListenHost": "localhost"` in `agent.json`).

## Troubleshooting

| symptom | likely cause |
|---|---|
| `agent not reachable` | VM off, nobody logged on (the agent starts with the session), port closed |
| `rejected the token` | token in `.claude.json` differs from `C:\ProgramData\TiaAgent\agent.json` |
| `TIA Portal V.. not available` | no `Vxx\TiaMcpServer.exe` in the install folder: rebuild on the VM and reinstall |
| `tia_attach` waits | the Openness confirmation is open on the VM desktop |
| HttpListener "file in use" on start | the port is in a range Windows reserves (`netsh int ipv4 show excludedportrange protocol=tcp`): pick another |

Logs: `C:\ProgramData\TiaAgent\logs` — `agent-*.log` (every request) and `server-<session>.log`
(stderr of each TIA server).

`test\remote.ps1` exercises agent and client end to end with a fake server, without TIA Portal.
