"""Verify timed dry-run shutdown with an SSE subscriber and an active diagnostic.

Run only when no other PadHotkey server is running. Starts and owns one process;
never kills an unrelated server. --data must be a new dedicated directory.
"""
import argparse
import json
import subprocess
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", required=True, type=Path)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--port", type=int, default=17845)
    args = parser.parse_args()
    if args.data.exists():
        raise RuntimeError("--data must be a new dedicated directory; refusing existing content.")
    if not args.exe.is_file():
        raise RuntimeError("--exe must be the built PadHotkey executable.")
    base = f"http://127.0.0.1:{args.port}"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    token = ""
    stream_opened, stream_closed = threading.Event(), threading.Event()
    stream_lines = []
    stream_result = {}
    started = time.monotonic()
    process = subprocess.Popen(
        [str(args.exe.resolve()), "serve", "--backend", "dry-run", "--data", str(args.data.resolve()),
         "--port", str(args.port), "--seconds", "5"],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def request(method, path, body=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        headers = {"Authorization": f"Bearer {token}"}
        if data is not None:
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(base + path, data=data, headers=headers, method=method)
        with opener.open(req, timeout=1) as response:
            return json.loads(response.read())

    def read_stream():
        try:
            req = urllib.request.Request(base + "/v1/events/stream", headers={"Authorization": f"Bearer {token}"})
            with opener.open(req, timeout=12) as response:
                stream_opened.set()
                while True:
                    line = response.readline()
                    if not line:
                        break
                    # Keep only limited event text, never any authentication material.
                    if len(stream_lines) < 100:
                        stream_lines.append(line.decode("utf-8", errors="replace").strip())
            stream_result["end"] = "eof"
        except Exception as error:
            stream_result["end"] = type(error).__name__
        finally:
            stream_result["closedAfterSeconds"] = round(time.monotonic() - started, 3)
            stream_closed.set()

    try:
        deadline = started + 3
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise AssertionError(f"Server exited before readiness, exit={process.returncode}")
            token_file = args.data / "api-token.txt"
            if token_file.exists():
                token = token_file.read_text(encoding="utf-8-sig").strip()
                try:
                    if request("GET", "/v1/status")["backend"] == "dry-run":
                        break
                except (urllib.error.URLError, TimeoutError):
                    pass
            time.sleep(.025)
        else:
            raise AssertionError("Dry-run server did not become ready within 3 seconds")
        request("PUT", "/v1/runtime", {"paused": True})
        stream_thread = threading.Thread(target=read_stream, daemon=True)
        stream_thread.start()
        if not stream_opened.wait(1):
            raise AssertionError("SSE subscriber failed to connect")
        request("POST", "/v1/diagnostics", {"keys": ["RightAlt"], "mode": "hold", "durationMs": 12000, "delayMs": 100})
        deadline = time.monotonic() + 1
        while time.monotonic() < deadline:
            state = request("GET", "/v1/status")
            if state["dryRunKeys"] == ["RightAlt"]:
                break
            time.sleep(.025)
        else:
            raise AssertionError("Hold diagnostic did not activate before shutdown")
        print("PASS SSE subscribed and 12-second hold active", flush=True)
        output, _ = process.communicate(timeout=max(.1, started + 9 - time.monotonic()))
        elapsed = time.monotonic() - started
        if process.returncode != 0:
            raise AssertionError(f"Server exit={process.returncode}; output: {output[-1000:]}")
        if not 4 <= elapsed < 9:
            raise AssertionError(f"Expected approximately 5-second exit, observed {elapsed:.3f}s")
        if not stream_closed.wait(.5):
            raise AssertionError("SSE remained open after process exit")
        if stream_result["closedAfterSeconds"] >= 9:
            raise AssertionError("SSE did not close promptly")
        release_seen = any("diagnostic-ended-keys-released" in line for line in stream_lines)
        print(f"PASS timed process exit in {elapsed:.3f}s", flush=True)
        print(f"PASS SSE closed in {stream_result['closedAfterSeconds']:.3f}s ({stream_result['end']})", flush=True)
        print("Release event observed via SSE" if release_seen else
              "Release event not observed before stream closure; this dry-run check does not prove physical key release", flush=True)
        print("3/3 shutdown checks passed", flush=True)
    finally:
        if process.poll() is None:
            # This is exclusively the process created above, and it uses dry-run.
            process.terminate()
            try:
                process.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.communicate(timeout=3)


if __name__ == "__main__":
    main()
