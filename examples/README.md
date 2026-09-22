# Wiring it into an MCP client

The server speaks MCP over stdin/stdout. Every client below launches it the same
way — the only differences are where the config file lives and what the key is
called.

Use the **absolute path** to `bin\TiaMcpServer.exe`, and escape the backslashes
in JSON.

## Claude Code

```cmd
claude mcp add tia -- "C:\Progetti\McpTia\bin\TiaMcpServer.exe"
```

Read-only, which is the sane default until you trust it:

```cmd
claude mcp add tia -- "C:\Progetti\McpTia\bin\TiaMcpServer.exe" --read-only
```

## Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\Progetti\\McpTia\\bin\\TiaMcpServer.exe",
      "args": []
    }
  }
}
```

## VS Code

`.vscode/mcp.json` in the workspace

```json
{
  "servers": {
    "tia": {
      "type": "stdio",
      "command": "C:\\Progetti\\McpTia\\bin\\TiaMcpServer.exe",
      "args": ["--read-only"]
    }
  }
}
```

## Environment variables

| variable | effect |
|---|---|
| `TIA_OPENNESS_PATH` | folders holding the Openness assemblies, `;`-separated. Set it when several TIA Portal versions are installed and you want a specific one. |
| `TIA_MCP_READONLY=1` | same as `--read-only`. Useful when the client config is not yours to edit. |

Example, pinning V21:

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\Progetti\\McpTia\\bin\\TiaMcpServer.exe",
      "env": {
        "TIA_OPENNESS_PATH": "C:\\Program Files\\Siemens\\Automation\\Portal V21\\PublicAPI\\V21\\net48"
      }
    }
  }
}
```

## Two servers, one machine

Running a read-only server alongside a writable one is a reasonable habit: point
the model at the read-only one for exploring, and switch when you actually mean
to change something.

```json
{
  "mcpServers": {
    "tia": {
      "command": "C:\\Progetti\\McpTia\\bin\\TiaMcpServer.exe",
      "args": ["--read-only"]
    },
    "tia-write": {
      "command": "C:\\Progetti\\McpTia\\bin\\TiaMcpServer.exe"
    }
  }
}
```

They are separate processes, so each holds its own Openness session — and each
costs its own confirmation when it attaches. Attach only the one you are using.

## Checking it works

Before involving a client at all:

```cmd
powershell -ExecutionPolicy Bypass -File test\smoke.ps1
```

It stops at `tia_instances`, so it raises no confirmation dialog and touches no
project. If that prints a tool list and finds your running TIA Portal, the
server is fine and anything that goes wrong afterwards is configuration.

## If the client shows no tools

1. Run `smoke.ps1`. If it fails there, it is not the client.
2. Check the path in the config is absolute and the backslashes are escaped.
3. Look at the client's MCP log: the server writes its diagnostics to **stderr**
   and keeps stdout strictly for protocol.
4. `tia_session` reports which folder the Openness assemblies came from and
   everywhere else it looked — that usually settles it.
