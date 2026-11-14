import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import {
  api,
  ApiError,
  authHeaders,
  field,
  rows,
  setCredential,
  onSessionExpired,
} from "@/api/client";
import { ErrorNotice } from "@/components/workspace";
import { LocaleProvider } from "@/locales/i18n";
afterEach(() => {
  vi.unstubAllGlobals();
  cleanup();
  setCredential({});
  localStorage.clear();
});
describe("administration contract adapter", () => {
  it("accepts wrapped and unwrapped contracts and PascalCase rows", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(
          Response.json({ code: 200, data: { Items: [{ Id: 1 }] } }),
        )
        .mockResolvedValueOnce(
          Response.json({ items: [{ pluginId: "cache" }], totals: {} }),
        ),
    );
    expect(field(rows(await api("api/routes"))[0], "id")).toBe(1);
    expect(rows(await api("api/plugin-resources"))[0].pluginId).toBe("cache");
  });
  it("rejects HTTP-200 application errors without persisting credentials", async () => {
    const fetcher = vi
      .fn()
      .mockResolvedValue(
        Response.json({ code: 401, message: "Invalid credentials" }),
      );
    vi.stubGlobal("fetch", fetcher);
    setCredential({ "X-Api-Key": "memory-only" });
    await expect(api("login")).rejects.toMatchObject({ status: 401 });
    expect(fetcher.mock.calls[0][1].headers["X-Api-Key"]).toBe("memory-only");
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
  it("does not report partial batch or notification failures as success", async () => {
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValueOnce(
          Response.json({
            code: 200,
            message: "One binding failed",
            data: { succeeded: 1, failed: 1, items: [] },
          }),
        )
        .mockResolvedValueOnce(
          Response.json({
            code: 200,
            data: { success: false, details: [{ error: "Delivery failed" }] },
          }),
        ),
    );
    await expect(api("api/plugin-bindings/batch-create")).rejects.toMatchObject(
      { message: "One binding failed", result: { failed: 1 } },
    );
    await expect(api("api/webhook/test")).rejects.toBeInstanceOf(ApiError);
  });
  it("reports HTTP 401 to the session boundary", async () => {
    const expired = vi.fn();
    onSessionExpired(expired);
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          Response.json({ message: "Unauthorized" }, { status: 401 }),
        ),
    );
    await expect(api("api/routes")).rejects.toBeInstanceOf(ApiError);
    expect(expired).toHaveBeenCalledOnce();
  });
  it.each([
    [400, "ValidationRejected", "Validation rejected"],
    [409, "VersionConflict", "Version conflict"],
    [503, "StorageFailed", "Storage failure"],
    [500, "ActivationFailed", "Activation failure"],
  ])("renders %s failures and both revisions", (code, status, label) => {
    render(
      <LocaleProvider>
        <ErrorNotice
          error={
            new ApiError(Number(code), {
              status,
              message: "Rejected",
              activeVersion: 7,
              persistedVersion: 8,
            })
          }
        />
      </LocaleProvider>,
    );
    expect(screen.getByRole("alert")).toHaveTextContent(String(label));
    expect(screen.getByText("Your draft is preserved.")).toBeVisible();
    expect(screen.getByText("Stored revision: 8")).toBeVisible();
    expect(screen.getByText("Active revision: 7")).toBeVisible();
  });
  it("uses English when a saved locale is unsupported", () => {
    localStorage.setItem("gateway-locale", "fr-FR");
    render(
      <LocaleProvider>
        <ErrorNotice
          error={
            new ApiError(409, {
              status: "VersionConflict",
              message: "Conflict",
            })
          }
        />
      </LocaleProvider>,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Version conflict");
    expect(localStorage.getItem("gateway-locale")).toBe("en-US");
    expect(document.documentElement.lang).toBe("en-US");
    expect(authHeaders()["X-Gateway-Locale"]).toBe("en-US");
  });
});
