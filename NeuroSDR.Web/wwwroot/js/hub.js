/** @typedef {import('./types.js').RadioRemoteSnapshot} RadioRemoteSnapshot */
/** @typedef {import('./types.js').SpectrumRemoteFrame} SpectrumRemoteFrame */
/** @typedef {import('./types.js').AfPluginRemoteEvent} AfPluginRemoteEvent */

/**
 * @param {string} [accessToken]
 * @returns {import('@microsoft/signalr').HubConnection}
 */
export function createHub(accessToken = "") {
  const builder = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/radio" + (accessToken ? `?access_token=${encodeURIComponent(accessToken)}` : ""), {
      accessTokenFactory: accessToken ? () => accessToken : undefined
    })
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning);
  return builder.build();
}

export function readToken() {
  const q = new URLSearchParams(location.search).get("token")
    || new URLSearchParams(location.search).get("access_token");
  if (q) {
    localStorage.setItem("neurosdr.token", q);
    return q;
  }
  return localStorage.getItem("neurosdr.token") || "";
}
