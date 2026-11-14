export type JsonObject = Record<string, unknown>;
export interface AppConfig {
  uiBase: string;
  apiBase: string;
  authMode: string;
  apiKeyHeader: string;
  locale: string;
  ai: boolean;
  mcp: boolean;
}
export const config: AppConfig = JSON.parse(
  document.getElementById("app-config")?.textContent ??
    JSON.stringify({
      uiBase: "",
      apiBase: "/wafish",
      authMode: import.meta.env.VITE_AUTH_MODE ?? "DefaultJwt",
      apiKeyHeader: "X-Api-Key",
      locale: "en-US",
      ai: true,
      mcp: false,
    }),
);
export const requestCredentials = (): RequestCredentials =>
  ["BasicAuth", "ApiKey"].includes(config.authMode) ? "omit" : "same-origin";
let credential: Record<string, string> = {};
let onUnauthorized = () => {};
export const setCredential = (value: Record<string, string>) => {
  credential = value;
};
export const authHeaders = () => ({
  ...credential,
  "X-Gateway-Locale": "en-US",
});
export const onSessionExpired = (callback: () => void) => {
  onUnauthorized = callback;
};
export const object = (value: unknown): JsonObject =>
  value !== null && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonObject)
    : {};
export function field(value: unknown, name: string): unknown {
  const obj = object(value);
  return obj[
    Object.keys(obj).find((k) => k.toLowerCase() === name.toLowerCase()) ?? name
  ];
}
export function text(value: unknown, fallback = "—"): string {
  return value == null
    ? fallback
    : typeof value === "object"
      ? JSON.stringify(value)
      : String(value);
}
export function rows(value: unknown): JsonObject[] {
  const list = Array.isArray(value)
    ? value
    : (field(value, "items") ??
      field(value, "records") ??
      field(value, "logs") ??
      field(value, "entries") ??
      []);
  return Array.isArray(list) ? list.map(object) : [];
}
export function setField(value: JsonObject, name: string, data: unknown) {
  const key =
    Object.keys(value).find((k) => k.toLowerCase() === name.toLowerCase()) ??
    name;
  return { ...value, [key]: data };
}
export class ApiError extends Error {
  status: number;
  result: JsonObject;
  constructor(status: number, result: JsonObject) {
    super(
      text(
        field(result, "message") ?? field(result, "title"),
        `HTTP ${status}`,
      ),
    );
    this.status = status;
    this.result = result;
  }
}
export async function api<T = unknown>(
  path: string,
  init: RequestInit = {},
): Promise<T> {
  const response = await fetch(config.apiBase + "/" + path.replace(/^\//, ""), {
    ...init,
    credentials: requestCredentials(),
    headers: {
      ...authHeaders(),
      ...(init.body ? { "Content-Type": "application/json" } : {}),
      ...init.headers,
    },
  });
  const data: unknown =
    response.status === 204
      ? {}
      : await response.json().catch(() => ({ message: response.statusText }));
  const code = Number(field(data, "code") ?? response.status);
  if (
    !response.ok ||
    code >= 400 ||
    field(data, "success") === false ||
    field(field(data, "data"), "success") === false ||
    field(data, "ok") === false ||
    Number(field(field(data, "data"), "failed") ?? 0) > 0
  ) {
    if (response.status === 401 && !path.includes("login")) onUnauthorized();
    throw new ApiError(
      response.status >= 400 ? response.status : code >= 400 ? code : 400,
      { ...object(data), ...object(field(data, "data")) },
    );
  }
  return (field(data, "data") ?? data) as T;
}
export const write = <T = unknown>(
  path: string,
  method: string,
  body?: unknown,
) =>
  api<T>(path, {
    method,
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
export async function download(path: string, filename: string) {
  const response = await fetch(config.apiBase + "/" + path, {
    credentials: requestCredentials(),
    headers: authHeaders(),
  });
  if (!response.ok)
    throw new ApiError(response.status, { message: response.statusText });
  saveBlob(await response.blob(), filename);
}
export function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
export const idPath = (id: string) => encodeURIComponent(id);
