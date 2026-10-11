"""Run with python tests/smoke.py after dotnet build -c Release."""
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = "http://127.0.0.1:5188"


def request(method, path, data=None):
    body = None if data is None else json.dumps(data).encode()
    req = urllib.request.Request(
        BASE + path, data=body, method=method,
        headers={"Content-Type": "application/json"} if body else {},
    )
    try:
        with urllib.request.urlopen(req, timeout=5) as response:
            payload = response.read()
            return response.status, response.headers, json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        payload = error.read()
        return error.code, error.headers, json.loads(payload) if payload else None


sample = {
    "name": "Ada Lovelace", "email": "ada@example.com", "phone": "+90 555 123 4567",
    "address": {"street": "1 Example Street", "city": "Izmir", "country": "Turkey"},
    "notes": "Classmate", "isFavorite": False,
}

creationflags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
process = subprocess.Popen(
    ["dotnet", "run", "--no-build", "-c", "Release", "--urls", BASE],
    cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    creationflags=creationflags,
)
try:
    for _ in range(60):
        try:
            if request("GET", "/health")[0] == 200:
                break
        except Exception:
            time.sleep(0.25)
    else:
        raise RuntimeError("API did not start")

    status, _, schema = request("GET", "/openapi.json")
    assert status == 200 and schema["openapi"].startswith("3.")
    status, headers, created = request("POST", "/api/v1/contacts", sample)
    assert status == 201 and headers["Location"].endswith(created["id"])
    contact_id = created["id"]
    assert created["createdAt"] == created["updatedAt"]
    assert request("GET", f"/api/v1/contacts/{contact_id}")[2]["name"] == "Ada Lovelace"

    second = sample | {"name": "Grace Hopper", "email": "grace@example.com"}
    assert request("POST", "/api/v1/contacts", second)[0] == 201
    status, _, page = request("GET", "/api/v1/contacts?search=izmir&sortBy=name&order=desc&page=1&pageSize=1")
    assert status == 200 and page["total"] == 2 and page["items"][0]["name"] == "Grace Hopper"
    assert request("GET", "/api/v1/contacts?pageSize=101")[0] == 400

    status, _, favorite = request("PATCH", f"/api/v1/contacts/{contact_id}/favorite", {"isFavorite": True})
    assert status == 200 and favorite["isFavorite"] is True
    assert request("PATCH", f"/api/v1/contacts/{contact_id}/favorite", {"isFavorite": True})[2] == favorite
    assert request("GET", "/api/v1/contacts?favorite=true")[2]["total"] == 1
    assert request("PATCH", f"/api/v1/contacts/{contact_id}/favorite", {})[0] == 400

    replacement = sample | {"name": "Ada Byron", "isFavorite": False}
    status, _, updated = request("PUT", f"/api/v1/contacts/{contact_id}", replacement)
    assert status == 200 and updated["name"] == "Ada Byron" and not updated["isFavorite"]
    assert updated["createdAt"] == created["createdAt"]
    assert request("PUT", f"/api/v1/contacts/{contact_id}", replacement)[2] == updated
    assert request("POST", "/api/v1/contacts", sample | {"email": "bad"})[0] == 400
    assert request("DELETE", f"/api/v1/contacts/{contact_id}")[0] == 204
    assert request("GET", f"/api/v1/contacts/{contact_id}")[0] == 404
    print("Smoke test passed: schema, CRUD, search, sorting, pagination, favorites, validation.")
finally:
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
