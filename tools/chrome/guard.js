(() => {
  // Write guard for the APCOA Flow web app in the MCP-driven Chrome: blocks every non-GET call to the API, so
  // browsing can never start, extend or stop a session by accident. Re-run after every navigation or reload.
  if (window.__guard) return "guard already on, blocked so far: " + window.__guard.blocked.length;
  const g = window.__guard = { blocked: [] };
  const api = "api.nucleus.apcoaflow.com"; // the API host (see src/ApcoaFlowApiClient/ApcoaFlowEndpoints.cs)
  const bad = (m, u) => String(u).includes(api) && !["GET", "OPTIONS"].includes(String(m).toUpperCase());
  const open = XMLHttpRequest.prototype.open, send = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (m, u) { this.__gm = m; this.__gu = u; return open.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function () {
    if (bad(this.__gm, this.__gu)) { g.blocked.push([this.__gm, String(this.__gu), new Date().toISOString()]); throw new Error("write guard: blocked " + this.__gm + " " + this.__gu); }
    return send.apply(this, arguments);
  };
  const f = window.fetch;
  window.fetch = function (input, init) {
    const m = (init && init.method) || (input && input.method) || "GET", u = (input && input.url) || input;
    if (bad(m, u)) { g.blocked.push([m, String(u), new Date().toISOString()]); return Promise.reject(new Error("write guard: blocked " + m + " " + u)); }
    return f.apply(this, arguments);
  };
  return "guard on: non-GET calls to the APCOA Flow API are blocked";
})()
