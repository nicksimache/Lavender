"""Offline stdio integration check: tools register and reject calls without a selected project."""
import json
import queue
import subprocess
import sys
import threading
from pathlib import Path

root = Path(__file__).resolve().parents[1]
server = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "artifacts/file-tools-check/Lavender.McpServer.dll"
process = subprocess.Popen(["dotnet", str(server)], cwd=root, stdin=subprocess.PIPE,
                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
                           creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
messages = queue.Queue()

def collect():
    for line in process.stdout:
        messages.put(json.loads(line))

threading.Thread(target=collect, daemon=True).start()

def send(message):
    process.stdin.write(json.dumps(message) + "\n")
    process.stdin.flush()

def request(number, method, params):
    send(dict(jsonrpc="2.0", id=number, method=method, params=params))
    while True:
        reply = messages.get(timeout=15)
        if reply.get("id") == number:
            assert "error" not in reply, reply
            return reply["result"]

try:
    request(1, "initialize", dict(protocolVersion="2025-03-26", capabilities={},
                                  clientInfo=dict(name="file-tools-smoke", version="1")))
    send(dict(jsonrpc="2.0", method="notifications/initialized"))
    listed = request(2, "tools/list", {})
    tools = {tool["name"]: tool for tool in listed["tools"]}
    expected = {"lavender_read_file", "lavender_create_file", "lavender_delete_file", "lavender_move_file", "lavender_write_lines"}
    assert expected <= tools.keys(), expected - tools.keys()
    assert "expectedContentHash" in tools["lavender_write_lines"]["inputSchema"]["required"]
    for number, (name, arguments) in enumerate([
        ("lavender_read_file", dict(path="test.cs")),
        ("lavender_create_file", dict(path="test.cs", content="fixture")),
        ("lavender_delete_file", dict(path="test.cs", expectedContentHash="invalid")),
        ("lavender_move_file", dict(path="test.cs", destinationPath="moved.cs", expectedContentHash="invalid")),
        ("lavender_write_lines", dict(path="test.cs", startLine=1, endLine=1, replacementCode="fixture", expectedContentHash="invalid")),
    ], start=3):
        result = request(number, "tools/call", dict(name=name, arguments=arguments))
        payload = result.get("structuredContent")
        if payload is None:
            payload = json.loads(next(c["text"] for c in result["content"] if c.get("type") == "text"))
        assert payload["success"] is False and payload["changed"] is False, payload
        assert "Open a project" in payload["message"], payload
    print("PASS: five file tools registered with required hashes and project-selection guards")
finally:
    process.terminate()
    process.wait(timeout=10)
