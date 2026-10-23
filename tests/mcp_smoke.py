# KSP Wingman · tests/mcp_smoke.py
# Talks MCP over stdio to KspWingman.Mcp like an agent's host would: initialize, list the tools, call some.
# Usage: python tests/mcp_smoke.py <path to KspWingman.Mcp.exe> <kRPC port> tool[:json args] ...
#   e.g. python tests/mcp_smoke.py src/KspWingman.Mcp/bin/Debug/net10.0/KspWingman.Mcp.exe 50002 status players "send_chat:{\"text\":\"hi\"}"

import json, os, subprocess, sys

exe, port, calls = sys.argv[1], sys.argv[2], sys.argv[3:]
env = dict(os.environ, WINGMAN_KRPC_PORT=port)
proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env, text=True, encoding="utf-8")
next_id = 0

def request(method, params=None):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params or {}}) + "\n")
    proc.stdin.flush()
    while True:
        line = proc.stdout.readline()
        if not line:
            raise SystemExit("the server closed")
        message = json.loads(line)
        if message.get("id") == next_id:
            return message

init = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "mcp_smoke", "version": "0"}})
print("server:", init["result"]["serverInfo"])
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
proc.stdin.flush()
tools = request("tools/list")["result"]["tools"]
print("tools:", ", ".join(t["name"] for t in tools))
for call in calls:
    name, _, args = call.partition(":")
    result = request("tools/call", {"name": name, "arguments": json.loads(args) if args else {}})
    if "error" in result:
        print(f"== {name}: protocol error {result['error']}")
        continue
    body = result["result"]
    text = "\n".join(c.get("text", "") for c in body.get("content", []))
    print(f"== {name}{' (tool error)' if body.get('isError') else ''}:\n{text}")
proc.stdin.close()
proc.wait(timeout=10)
