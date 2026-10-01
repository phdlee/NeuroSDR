/** Typed mirror of wwwroot/js/hub.js — keep in sync when hub methods change. */
import type { RadioHubMethods } from "./types";

declare const signalR: typeof import("@microsoft/signalr");

export function readToken(): string {
  const q = new URLSearchParams(location.search).get("token")
    || new URLSearchParams(location.search).get("access_token");
  if (q) {
    localStorage.setItem("neurosdr.token", q);
    return q;
  }
  return localStorage.getItem("neurosdr.token") || "";
}

export function createHub(accessToken = "") {
  return new signalR.HubConnectionBuilder()
    .withUrl("/hubs/radio" + (accessToken ? `?access_token=${encodeURIComponent(accessToken)}` : ""))
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}

export type { RadioHubMethods };
