#!/bin/sh
# login-inject.sh [OUT_DIR] : sign in headless (password grant, no bot check) and write OUT_DIR/inject-token.js, a
# script that puts the refresh token into the APCOA Flow web app's localStorage the way the app stores it. Use it to
# drive the real web app in the Chrome DevTools MCP when a flow hasn't been seen on the wire yet; then write a sandbox
# scenario that replays the calls (CLAUDE.md, "Debugging API / MCP issues").
#
#   1. tools/chrome/login-inject.sh                     (OUT_DIR defaults to src/ApcoaFlow.Sandbox/out: gitignored,
#                                                        and inside the Chrome MCP's workspace root)
#   2. open https://app.apcoaflow.com/ in the MCP page, evaluate_script(sourcePath=OUT_DIR/inject-token.js)
#   3. rm OUT_DIR/inject-token.js, navigate_page to https://app.apcoaflow.com/ (the app refreshes the token itself)
#   4. evaluate_script(sourcePath=OUT_DIR/guard.js) to block writes; re-run after every navigation or reload
#
# Signing in by hand in the MCP Chrome fails on the bot check (Cloudflare Turnstile), hence this. Each run signs in
# again (~7 s). Never copy a refresh token from another browser: it rotates on first use and logs that browser out.
# How the app stores it: localStorage["FlutterSecureStorage"] = base64 raw AES-256 key;
# localStorage["FlutterSecureStorage.auth-refresh-token"] = base64(iv12) + "." + base64(AES-GCM(token)).
#
# Credentials: env APCOAFLOW_EMAIL + APCOAFLOW_PASSWORD, else user-secrets id apcoa-flow-api-tests
# (ApcoaFlow:Email / ApcoaFlow:Password, shared with the live tests and the sandbox). Never prints the token.
set -e
here=$(cd "$(dirname "$0")" && pwd)
out=${1:-"$here/../../src/ApcoaFlow.Sandbox/out"}
mkdir -p "$out"
cp "$here/guard.js" "$out/guard.js"
if [ -z "$APCOAFLOW_EMAIL" ] || [ -z "$APCOAFLOW_PASSWORD" ]; then
  secrets=$(dotnet user-secrets list --id apcoa-flow-api-tests)
else
  secrets=""
fi
printf '%s\n' "$secrets" | python3 -c '
import sys, os, json, urllib.request, urllib.parse, urllib.error
d = dict(l.rstrip("\n").split(" = ", 1) for l in sys.stdin if " = " in l)
user = os.environ.get("APCOAFLOW_EMAIL") or d.get("ApcoaFlow:Email")
pw = os.environ.get("APCOAFLOW_PASSWORD") or d.get("ApcoaFlow:Password")
if not user or not pw: sys.exit("no credentials (APCOAFLOW_EMAIL/APCOAFLOW_PASSWORD or user-secrets id apcoa-flow-api-tests)")
body = urllib.parse.urlencode({"grant_type": "password", "username": user, "password": pw,
    "client_id": "flownextapp.prd.nucleus.valuespaces.io", "scope": "offline_access"}).encode()
req = urllib.request.Request("https://auth.prd.nucleus.valuespaces.io/connect/token", body,
    {"Content-Type": "application/x-www-form-urlencoded"})
try: r = json.load(urllib.request.urlopen(req, timeout=120))
except urllib.error.HTTPError as e: sys.exit("sign-in failed: HTTP %d %s" % (e.code, e.read()[:200]))
js = """async () => {
 const rt = %s;
 const raw = crypto.getRandomValues(new Uint8Array(32)), iv = crypto.getRandomValues(new Uint8Array(12));
 const b64 = u => btoa(String.fromCharCode(...u));
 const k = await crypto.subtle.importKey("raw", raw, "AES-GCM", false, ["encrypt"]);
 const ct = new Uint8Array(await crypto.subtle.encrypt({name: "AES-GCM", iv}, k, new TextEncoder().encode(rt)));
 localStorage.setItem("FlutterSecureStorage", b64(raw));
 localStorage.setItem("FlutterSecureStorage.auth-refresh-token", b64(iv) + "." + b64(ct));
 localStorage.setItem("flutter.gdprConsentKey", "true");
 localStorage.setItem("flutter.isFirstRun", "false");
 return "injected, token len " + rt.length + "; now delete this file and reload the app";
}""" % json.dumps(r["refresh_token"])
p = sys.argv[1]
fd = os.open(p, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, "w") as f: f.write(js)
print("signed in as", user.split("@")[0][:2] + "…; wrote", p)
' "$out/inject-token.js"
