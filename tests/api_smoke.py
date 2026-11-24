"""Black-box checks against a running PadHotkey dry-run server; never sends HID input.

Run: python tests/api_smoke.py --data <server-data-directory> --port 17843
The original profile is restored using a fresh revision; previous revisions remain
in the server's normal backups. The server finishes paused.
"""
import argparse
import copy
import json
import time
import urllib.error
import urllib.request
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--port", required=True, type=int)
    args = parser.parse_args()
    token = (args.data / "api-token.txt").read_text(encoding="utf-8-sig").strip()
    base = f"http://127.0.0.1:{args.port}"
    # Ignore proxy environment variables: this test targets loopback only.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    passed = 0

    def request(method, path, body=None, *, expected=200, auth=True, headers=None):
        request_headers = {"Authorization": f"Bearer {token}"} if auth else {}
        if headers:
            request_headers.update(headers)
        data = None if body is None else json.dumps(body).encode("utf-8")
        if data is not None:
            request_headers["Content-Type"] = "application/json"
        req = urllib.request.Request(base + path, data=data, headers=request_headers, method=method)
        try:
            response = opener.open(req, timeout=10)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status = response.status
            raw = response.read()
            response_headers = dict(response.headers)
        if status != expected:
            raise AssertionError(f"{method} {path}: expected HTTP {expected}, got {status}")
        return (json.loads(raw) if raw else None), response_headers

    def check(name, condition=True):
        nonlocal passed
        if not condition:
            raise AssertionError(name)
        passed += 1
        print(f"PASS {name}", flush=True)

    def status():
        return request("GET", "/v1/status")[0]

    def profile():
        return request("GET", "/v1/profile")[0]

    def put_profile(value, revision):
        return request("PUT", "/v1/profile", value, headers={"If-Match": f'"{revision}"'})[0]

    def wait_for(predicate, description, seconds=4):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            value = status()
            if predicate(value):
                return value
            time.sleep(0.025)
        raise AssertionError(f"Timed out waiting for {description}")

    initial_status = status()
    if initial_status["backend"] != "dry-run":
        raise RuntimeError("Refusing API smoke tests against a real HID backend.")
    original = copy.deepcopy(profile())
    try:
        request("PUT", "/v1/runtime", {"paused": True})
        request("GET", "/v1/status", expected=401, auth=False)
        check("missing authorization rejected")
        request("GET", "/v1/status", expected=403, headers={"Origin": "https://example.invalid"})
        check("browser Origin rejected")
        request("PUT", "/v1/runtime", {}, expected=400)
        check("empty runtime object cannot resume", status()["paused"] is True)

        before = profile()
        bad = copy.deepcopy(before)
        bad["bindings"] = [{"id": "bad", "input": "B", "mode": "hold", "keys": ["UnknownKey"]}]
        request("PUT", "/v1/profile", bad, expected=400,
                headers={"If-Match": f'"{before["version"]}"'})
        check("invalid key leaves profile and revision unchanged", profile() == before)

        # Isolate subsequent runtime tests from any connected physical controller.
        isolated = copy.deepcopy(before)
        isolated["bindings"] = []
        isolated = put_profile(isolated, before["version"])
        binding = {"input": "B", "mode": "hold", "keys": ["RightAlt"],
                   "processes": ["pad-hotkey-api-smoke-no-such-process"]}
        changed, headers = request("PUT", "/v1/profile/bindings/b", binding,
                                   headers={"If-Match": f'"{isolated["version"]}"'})
        check("binding update increments revision", changed["version"] == isolated["version"] + 1)
        check("binding route establishes stable ID", changed["bindings"][0]["id"] == "b")
        request("PUT", "/v1/profile/bindings/b", binding, expected=412,
                headers={"If-Match": f'"{isolated["version"]}"'})
        check("stale revision rejected", profile() == changed)
        persisted = json.loads((args.data / "profile.json").read_text(encoding="utf-8-sig"))
        check("successful update persisted", persisted == changed)

        request("PUT", "/v1/runtime", {"paused": False})
        check("resume accepted", status()["paused"] is False)
        request("PUT", "/v1/runtime", {"paused": True})
        check("pause releases output", status()["paused"] is True and status()["dryRunKeys"] == [])

        request("POST", "/v1/diagnostics",
                {"keys": ["LeftCtrl", "F10"], "mode": "tap", "durationMs": 100, "delayMs": 100},
                expected=400)
        check("too-short chord diagnostic rejected")
        request("POST", "/v1/diagnostics",
                {"keys": ["RightAlt"], "mode": "hold", "durationMs": 500, "delayMs": 200}, expected=202)
        request("POST", "/v1/diagnostics",
                {"keys": ["RightAlt"], "mode": "hold", "durationMs": 500, "delayMs": 200}, expected=409)
        check("overlapping diagnostics rejected")
        wait_for(lambda s: s["dryRunKeys"] == ["RightAlt"], "virtual hold")
        check("diagnostic holds specified key in dry-run")
        wait_for(lambda s: s["diagnostic"]["state"] == "completed" and s["dryRunKeys"] == [], "timed release")
        check("timed diagnostic releases without user response")

        for _ in range(220):
            request("PUT", "/v1/runtime", {"paused": True})
        events = request("GET", "/v1/events")[0]
        check("event memory remains bounded", 0 < len(events) <= 200)
        sequences = [event["sequence"] for event in events]
        check("event sequences remain monotonic", sequences == sorted(set(sequences)))
        last = events[-1]["sequence"]
        check("event cursor excludes old events", request("GET", f"/v1/events?after={last}")[0] == [])
    finally:
        # Always leave outputs off; restore configuration, never restore active input.
        request("POST", "/v1/release-all")
        latest = profile()
        restored = put_profile(original, latest["version"])
        expected_original = copy.deepcopy(original)
        expected_original["version"] = restored["version"]
        if restored != expected_original:
            raise AssertionError("Original profile restore mismatch")
        print("RESTORED original profile; runtime paused", flush=True)
    print(f"{passed}/{passed} API checks passed", flush=True)


if __name__ == "__main__":
    main()
