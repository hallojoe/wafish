import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import {
  api, ApiError, authHeaders, config, download, field, idPath, object,
  onSessionExpired, requestCredentials, rows, saveBlob, setCredential,
  setField, text, write,
} from "@/api/client";
import { JsonView, parseObject } from "@/components/workspace";

const originalAuth = config.authMode;
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  vi.useRealTimers();
  config.authMode = originalAuth;
  setCredential({});
  onSessionExpired(() => {});
});

describe("API request boundaries", () => {
  it.each(["BasicAuth", "ApiKey"])("omits ambient cookies for %s", (mode) => {
    config.authMode = mode;
    expect(requestCredentials()).toBe("omit");
  });
  it("uses same-origin credentials for JWT sessions", () => {
    config.authMode = "DefaultJwt";
    expect(requestCredentials()).toBe("same-origin");
    setCredential({ Authorization: "Bearer memory-only" });
    expect(authHeaders()).toEqual({ Authorization: "Bearer memory-only", "X-Gateway-Locale": "en-US" });
  });
  it("serializes mutations and normalizes leading slashes", async () => {
    const fetcher = vi.fn().mockResolvedValue(new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetcher);
    await expect(write("/api/routes", "POST", { name: "route" })).resolves.toEqual({});
    expect(fetcher).toHaveBeenCalledWith(config.apiBase + "/api/routes", expect.objectContaining({
      method: "POST", body: '{"name":"route"}', headers: expect.objectContaining({ "Content-Type": "application/json" }),
    }));
    await write("api/routes/one", "DELETE");
    expect(fetcher.mock.calls[1][1]).not.toHaveProperty("body");
    expect(fetcher.mock.calls[1][1].headers).not.toHaveProperty("Content-Type");
  });
  it("preserves caller headers and cancellation", async () => {
    const signal = new AbortController().signal;
    const fetcher = vi.fn().mockResolvedValue(Response.json({ ok: true }));
    vi.stubGlobal("fetch", fetcher);
    await api("api/routes", { signal, headers: { "X-Request-ID": "test" } });
    expect(fetcher.mock.calls[0][1]).toMatchObject({ signal, headers: { "X-Request-ID": "test" } });
  });
  it("reports malformed error bodies without a JSON parse failure", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response("bad gateway", { status: 502, statusText: "Bad Gateway" })));
    await expect(api("api/routes")).rejects.toMatchObject({ status: 502, message: "Bad Gateway" });
  });
  it.each([{ success: false }, { ok: false }, { data: { success: false } }, { data: { failed: 1 } }])(
    "rejects failure envelopes: %j", async (body) => {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue(Response.json(body)));
      await expect(api("api/routes")).rejects.toMatchObject({ status: 400 });
    },
  );
  it("does not expire the existing session when login fails", async () => {
    const expired = vi.fn(); onSessionExpired(expired);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(Response.json({}, { status: 401 })));
    await expect(api("login")).rejects.toBeInstanceOf(ApiError);
    expect(expired).not.toHaveBeenCalled();
  });
  it("preserves transport errors", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("offline")));
    await expect(api("api/routes")).rejects.toThrow("offline");
  });
});

describe("contract normalization", () => {
  it("handles nulls, casing, collection envelopes, and immutable updates", () => {
    for (const value of [null, 1, "text", []]) expect(object(value)).toEqual({});
    expect(field({ RouteId: "one" }, "routeid")).toBe("one");
    expect(field(null, "missing")).toBeUndefined();
    for (const key of ["items", "records", "logs", "entries"])
      expect(rows({ [key]: [{ id: 1 }] })).toEqual([{ id: 1 }]);
    expect(rows([{ id: 1 }, null])).toEqual([{ id: 1 }, {}]);
    expect(rows({ items: "wrong" })).toEqual([]);
    expect(rows(null)).toEqual([]);
    const original = { Enabled: true };
    expect(setField(original, "enabled", false)).toEqual({ Enabled: false });
    expect(original.Enabled).toBe(true);
    expect(setField({}, "enabled", true)).toEqual({ enabled: true });
    expect(text(null)).toBe("—");
    expect(text(undefined, "empty")).toBe("empty");
    expect(text({ a: 1 })).toBe('{"a":1}');
    expect(text(false)).toBe("false");
    expect(idPath("a/b c")).toBe("a%2Fb%20c");
    expect(new ApiError(409, { Title: "Conflict" }).message).toBe("Conflict");
    expect(new ApiError(503, {}).message).toBe("HTTP 503");
  });
  it("rejects non-object JSON drafts", () => {
    expect(parseObject('{"a":1}')).toEqual({ a: 1 });
    for (const value of ["null", "[]", "1", "true", '"x"', "invalid"])
      expect(() => parseObject(value)).toThrow();
  });
  it("renders JSON as text in a keyboard-accessible read-only field", () => {
    render(<JsonView value={{ html: "<script>bad()</script>" }} />);
    const region = screen.getByRole("textbox", { name: "JSON data" });
    expect(region).toHaveAttribute("readonly");
    expect(region).toHaveValue(JSON.stringify({ html: "<script>bad()</script>" }, null, 2));
    expect(region.querySelector("script")).toBeNull();
  });
});

describe("downloads", () => {
  function mockDownload() {
    vi.useFakeTimers();
    const create = vi.fn().mockReturnValue("blob:test");
    const revoke = vi.fn();
    vi.stubGlobal("URL", Object.assign(class extends URL {}, { createObjectURL: create, revokeObjectURL: revoke }));
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});
    return { create, revoke, click };
  }
  it("downloads authenticated data and releases its object URL", async () => {
    const { create, revoke, click } = mockDownload();
    const fetcher = vi.fn().mockResolvedValue(new Response("backup"));
    vi.stubGlobal("fetch", fetcher);
    setCredential({ "X-Api-Key": "memory-only" });
    await download("api/settings/database/backup", "backup.db");
    expect(fetcher.mock.calls[0][1].headers["X-Api-Key"]).toBe("memory-only");
    expect(create).toHaveBeenCalledOnce(); expect(click).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(1000);
    expect(revoke).toHaveBeenCalledWith("blob:test");
  });
  it("rejects failed downloads", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 403, statusText: "Forbidden" })));
    await expect(download("backup", "backup.db")).rejects.toMatchObject({ status: 403 });
  });
  it("exports local blobs without fetching", () => {
    const { click, revoke } = mockDownload();
    saveBlob(new Blob(["{}"]), "settings.json");
    expect(click).toHaveBeenCalledOnce();
    vi.runAllTimers(); expect(revoke).toHaveBeenCalledOnce();
  });
});
