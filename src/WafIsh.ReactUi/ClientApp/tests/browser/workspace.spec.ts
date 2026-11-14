import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { createHmac } from "node:crypto";
const password = process.env.UI_TEST_PASSWORD!;
async function login(page: Page) {
  await page.goto("/console");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Overview", exact: true }),
  ).toBeVisible();
}
async function open(page: Page, path: string, heading: string) {
  await page.goto("/console" + path);
  await expect(
    page.getByRole("heading", { name: heading, exact: true }).first(),
  ).toBeVisible();
}
test("login rejects invalid credentials and restores cookie sessions on deep links", async ({
  page,
}) => {
  await page.goto("/console/configuration/routes");
  await page.getByLabel("Username").fill("admin");
  await page.getByLabel("Password").fill("incorrect-password");
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Invalid credentials");
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Routes", exact: true }).first(),
  ).toBeVisible();
  expect(
    await page.evaluate(() =>
      Object.keys(localStorage).filter((k) =>
        /token|credential|password/i.test(k),
      ),
    ),
  ).toEqual([]);
  await page.reload();
  await expect(
    page.getByRole("heading", { name: "Routes", exact: true }).first(),
  ).toBeVisible();
  const cookie = (await page.context().cookies()).find(
    (c) => c.name === "dashboard_token",
  );
  expect(cookie?.httpOnly).toBe(true);
});
test("all administration workspaces load without Razor", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  await login(page);
  for (const [path, heading] of [
    ["/configuration/routes", "Routes"],
    ["/configuration/clusters", "Clusters"],
    ["/configuration/history", "History"],
    ["/configuration/import", "Import & export"],
    ["/observe/logs", "Logs"],
    ["/observe/traffic", "Traffic"],
    ["/observe/destinations", "Destinations"],
    ["/observe/audit", "Audit"],
    ["/extensions/plugins", "Plugins"],
    ["/extensions/bindings", "Bindings"],
    ["/extensions/presets", "Presets"],
    ["/extensions/resources", "Resources"],
    ["/settings/system", "System"],
    ["/settings/notifications", "Notifications"],
    ["/settings/account", "Account"],
    ["/settings/integrations", "AI configuration"],
    ["/assistant", "Assistant"],
  ]) {
    await open(page, path, heading);
    await expect(page.getByRole("alert")).toHaveCount(0);
  }
  expect(errors).toEqual([]);
});
test("legacy pages and assets are unavailable while the React workspace remains available", async ({
  request,
}) => {
  for (const path of [
    "/aneiang",
    "/aneiang/routes",
    "/aneiang/clusters",
    "/aneiang/settings",
    "/_content/WafIsh.Dashboard/js/core/dashboard-api.js",
  ]) {
    expect((await request.get(path)).status()).toBe(404);
  }
  expect((await request.get("/aneiang/login")).status()).toBe(405);
  expect((await request.get("/console/configuration/routes")).status()).toBe(
    200,
  );
});
test("route and cluster CRUD preserve advanced fields and show applied state", async ({
  page,
}) => {
  await login(page);
  await open(page, "/configuration/clusters", "Clusters");
  await page.getByRole("button", { name: "New cluster", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByLabel("Name", { exact: true })
    .fill("browser-cluster");
  await page.getByLabel("Destination address · d1").fill("http://127.0.0.1:9/");
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await open(page, "/configuration/routes", "Routes");
  await page.getByRole("button", { name: "New route", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByLabel("Name", { exact: true })
    .fill("browser-route");
  await page
    .getByLabel("Cluster", { exact: true })
    .selectOption("browser-cluster");
  await page.getByLabel("Match path").fill("/browser/{**catch-all}");
  await page.getByRole("tab", { name: "Advanced JSON" }).click();
  const draft = JSON.parse(
    await page.getByRole("textbox", { name: "Advanced JSON" }).inputValue(),
  );
  draft.Metadata = { "retained-native": "yes" };
  draft.Transforms = [{ PathRemovePrefix: "/browser" }];
  await page
    .getByRole("textbox", { name: "Advanced JSON" })
    .fill(JSON.stringify(draft));
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const row = page.getByRole("row").filter({ hasText: "browser-route" });
  await row.getByRole("button", { name: "Edit", exact: true }).click();
  await page.getByLabel("Order", { exact: true }).fill("5");
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const exported = await (
    await page.request.get("/aneiang/api/config/export")
  ).json();
  const config = exported.data.ReverseProxy ?? exported.data.reverseProxy;
  const saved = (config.Routes ?? config.routes)["browser-route"];
  expect(saved.Metadata ?? saved.metadata).toMatchObject({
    "retained-native": "yes",
  });
  expect(saved.Transforms ?? saved.transforms).toHaveLength(1);
  await row.getByRole("button", { name: "Disable", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(row).toContainText("Disabled");
  await row.getByRole("button", { name: "Rename", exact: true }).click();
  await page.getByLabel("New name").fill("browser-renamed");
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Rename", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const renamed = page.getByRole("row").filter({ hasText: "browser-renamed" });
  await renamed.getByRole("button", { name: "Delete", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(renamed).toHaveCount(0);
  await open(page, "/configuration/clusters", "Clusters");
  const cluster = page.getByRole("row").filter({ hasText: "browser-cluster" });
  await cluster.getByRole("button", { name: "Delete", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(cluster).toHaveCount(0);
});
for (const [status, outcome, title] of [
  [400, "ValidationRejected", "Validation rejected"],
  [409, "VersionConflict", "Version conflict"],
  [503, "StorageFailed", "Storage failure"],
  [500, "ActivationFailed", "Activation failure"],
] as const) {
  test(`failed save ${outcome} preserves the editor and revision details`, async ({
    page,
  }) => {
    await login(page);
    await open(page, "/configuration/routes", "Routes");
    await page.getByRole("button", { name: "New route", exact: true }).click();
    await page
      .getByRole("dialog")
      .getByLabel("Name", { exact: true })
      .fill("retained-draft");
    await page.route("**/api/config/routes/retained-draft", (route) =>
      route.fulfill({
        status,
        json: {
          code: status,
          status: outcome,
          message: "Injected failure",
          persistedVersion: 11,
          activeVersion: 10,
        },
      }),
    );
    await page
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await expect(page.getByRole("alert")).toContainText(title);
    await expect(page.getByLabel("Name", { exact: true })).toHaveValue(
      "retained-draft",
    );
    await expect(page.getByRole("alert")).toContainText("Stored revision: 11");
    await expect(page.getByRole("alert")).toContainText("Active revision: 10");
  });
}
test("snapshot comparison, rollback, import validation and export work", async ({
  page,
}) => {
  await login(page);
  await open(page, "/configuration/history", "History");
  await page
    .getByLabel("Snapshot description")
    .fill("Browser recovery checkpoint");
  await page
    .getByRole("button", { name: "Create snapshot", exact: true })
    .click();
  const row = page
    .getByRole("row")
    .filter({ hasText: "Browser recovery checkpoint" });
  await expect(row).toBeVisible();
  await row.getByRole("button", { name: "Compare", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Compare", exact: true }),
  ).toBeVisible();
  await row.getByRole("button", { name: "Rollback", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await open(page, "/configuration/import", "Import & export");
  const downloaded = page.waitForEvent("download");
  await page.getByRole("button", { name: "Export configuration" }).click();
  expect((await downloaded).suggestedFilename()).toBe("gateway-config.json");
  const exported = await (
    await page.request.get("/aneiang/api/config/export")
  ).json();
  await page
    .getByRole("textbox", { name: "Advanced JSON" })
    .fill(JSON.stringify(exported.data));
  await page.getByRole("button", { name: "Validate", exact: true }).click();
  await expect(
    page.getByText("Configuration is valid.", { exact: true }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Import configuration", exact: true })
    .click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});
test("live connection can pause and reconnect", async ({ page }) => {
  await login(page);
  await page.screenshot({
    path: "test-results/desktop-overview.png",
    fullPage: true,
    animations: "disabled",
  });
  await expect(page.getByText("Connected", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Pause", exact: true }).click();
  await expect(page.getByText("Paused", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Resume", exact: true }).click();
  await expect(page.getByText("Connected", { exact: true })).toBeVisible();
});
test("expired sessions return to login and clear the authenticated view", async ({
  page,
}) => {
  await login(page);
  await page.route("**/api/routes", (route) =>
    route.fulfill({ status: 401, json: { message: "Unauthorized" } }),
  );
  await open(page, "/configuration/routes", "Routes").catch(() => {});
  await expect(
    page.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByText("Session expired. Sign in again.", { exact: true }),
  ).toBeVisible();
});
test("English desktop and mobile layouts pass automated accessibility checks", async ({
  page,
}) => {
  await login(page);
  for (const path of ["/", "/configuration/routes", "/settings/account"]) {
    await page.goto("/console" + path);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    const results = await new AxeBuilder({ page })
      .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
      .analyze();
    expect(results.violations).toEqual([]);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator("html")).toHaveAttribute("lang", "en-US");
  await expect(
    page.getByRole("heading", { name: "Account", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Open navigation" }).click();
  await page
    .getByRole("dialog")
    .getByRole("link", { name: "Configuration", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Routes", exact: true }).first(),
  ).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
  await page.getByRole("button", { name: "New route", exact: true }).click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(page.getByRole("dialog")).toContainText("Common fields");
  const results = await new AxeBuilder({ page })
    .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
    .analyze();
  expect(results.violations).toEqual([]);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(
    page.getByRole("button", { name: "New route", exact: true }),
  ).toBeFocused();
  await page.getByRole("button", { name: "Switch theme" }).click();
  expect(await page.locator("html").getAttribute("class")).toContain("dark");
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
  await page.screenshot({
    path: "test-results/mobile-english.png",
    fullPage: true,
    animations: "disabled",
  });
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.evaluate(() => {
    document.documentElement.style.fontSize = "200%";
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});
function totp(secret: string) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of secret)
    bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
  const bytes = [];
  for (let i = 0; i + 8 <= bits.length; i += 8)
    bytes.push(parseInt(bits.slice(i, i + 8), 2));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac("sha1", Buffer.from(bytes))
    .update(counter)
    .digest();
  const offset = digest[19] & 15;
  return ((digest.readUInt32BE(offset) & 0x7fffffff) % 1000000)
    .toString()
    .padStart(6, "0");
}
for (const [mode, port] of [
  ["BasicAuth", 5220],
  ["ApiKey", 5221],
] as const) {
  test(`${mode} credentials stay in memory and authorize realtime requests`, async ({
    page,
  }) => {
    await page.goto(`http://127.0.0.1:${port}/console`);
    if (mode === "BasicAuth")
      await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel(mode === "ApiKey" ? "API key" : "Password", { exact: true })
      .fill(password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Overview", exact: true }),
    ).toBeVisible();
    await expect(page.getByText("Connected", { exact: true })).toBeVisible();
    await page.getByRole("link", { name: "Extensions", exact: true }).click();
    await expect(
      page.getByText("Response Cache", { exact: true }),
    ).toBeVisible();
    expect(
      await page.evaluate(() =>
        Object.keys(localStorage).filter((k) =>
          /token|credential|password/i.test(k),
        ),
      ),
    ).toEqual([]);
    await page.reload();
    await expect(
      page.getByRole("button", { name: "Sign in", exact: true }),
    ).toBeVisible();
  });
}
test("plugin bindings, presets, and activation use real administration APIs", async ({
  page,
}) => {
  await login(page);
  await open(page, "/extensions/bindings", "Bindings");
  await page.getByRole("button", { name: "New binding", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByLabel("Plugin", { exact: true })
    .selectOption("response-cache");
  await page
    .getByRole("dialog")
    .getByLabel("Scope", { exact: true })
    .selectOption("Route");
  await page
    .getByRole("dialog")
    .getByLabel("Target", { exact: true })
    .selectOption("demo");
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const row = page.getByRole("row").filter({ hasText: "response-cache" });
  await expect(row).toBeVisible();
  await row.getByRole("button", { name: "Edit", exact: true }).click();
  await page
    .getByRole("textbox", { name: "Configuration JSON" })
    .fill('{"enabled":true,"ttlSeconds":25}');
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const bindings = await (
    await page.request.get("/aneiang/api/plugin-bindings")
  ).json();
  const binding = bindings.data.find(
    (b: Record<string, unknown>) =>
      (b.pluginId ?? b.PluginId) === "response-cache",
  );
  expect(JSON.parse(binding.configJson ?? binding.ConfigJson).ttlSeconds).toBe(
    25,
  );
  await open(page, "/extensions/presets", "Presets");
  await page.getByRole("button", { name: "Save preset", exact: true }).click();
  await page.getByRole("tab", { name: "Advanced JSON" }).click();
  await page.getByRole("textbox", { name: "Advanced JSON" }).fill(
    JSON.stringify({
      name: "Browser preset",
      pluginId: "response-cache",
      configJson: '{"enabled":true,"ttlSeconds":35}',
      schemaVersion: 1,
    }),
  );
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  const preset = page.getByRole("row").filter({ hasText: "Browser preset" });
  await preset
    .getByRole("button", { name: "Apply preset", exact: true })
    .click();
  await page.getByRole("button", { name: "Save changes", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await preset.getByRole("button", { name: "Delete", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(preset).toHaveCount(0);
  await open(page, "/extensions/bindings", "Bindings");
  await page
    .getByRole("row")
    .filter({ hasText: "response-cache" })
    .getByRole("button", { name: "Delete", exact: true })
    .click();
  const deletedBinding = page.waitForResponse(
    (response) => response.request().method() === "DELETE" &&
      response.url().includes("/api/plugin-bindings/"),
  );
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  expect((await deletedBinding).ok()).toBeTruthy();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(
    page.getByRole("row").filter({ hasText: "response-cache" }),
  ).toHaveCount(0);
  await open(page, "/extensions/plugins", "Plugins");
  const card = page.locator('[data-slot="card"]').filter({
    has: page.getByRole("heading", { name: "Response Cache", exact: true }),
  });
  await card.getByRole("button", { name: "Disable", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(
    card.getByRole("button", { name: "Enable", exact: true }),
  ).toBeVisible();
  await card.getByRole("button", { name: "Enable", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(
    card.getByRole("button", { name: "Disable", exact: true }),
  ).toBeVisible();
});
test("two-factor setup, login challenge, and disable work", async ({
  page,
}) => {
  await login(page);
  await open(page, "/settings/account", "Account");
  await page.getByRole("button", { name: "Set up", exact: true }).click();
  const secret = await page.getByLabel("Secret", { exact: true }).inputValue();
  await page.getByLabel("Verification code").fill(totp(secret));
  await page.getByRole("button", { name: "Verify and enable" }).click();
  await expect(
    page.getByRole("button", { name: "Disable", exact: true }),
  ).toBeVisible();
  try {
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await page.getByLabel("Username").fill("admin");
    await page.getByLabel("Password").fill(password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.getByLabel("Verification code")).toBeVisible();
    await page.getByLabel("Verification code").fill(totp(secret));
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Account", exact: true }),
    ).toBeVisible();
  } finally {
    await page.request.post("/aneiang/api/2fa/disable");
  }
  await page.reload();
  await expect(
    page.getByRole("button", { name: "Set up", exact: true }),
  ).toBeVisible();
});
test("assistant write tools require confirmation and retain returned results", async ({
  page,
}) => {
  await login(page);
  let executed = 0;
  await page.route("**/api/ai/chat", (route) =>
    route.fulfill({
      contentType: "text/event-stream",
      body: 'data: {"type":"session","sessionId":"browser-ai"}\n\ndata: {"type":"chunk","content":"Review this change."}\n\ndata: {"type":"tool_call","toolName":"test_write","toolArgs":"{}","isWriteTool":true}\n\ndata: {"type":"done"}\n\n',
    }),
  );
  await page.route("**/api/ai/tool/execute", (route) => {
    executed++;
    return route.fulfill({
      json: { code: 200, data: { result: "confirmed" } },
    });
  });
  await page.route("**/api/ai/sessions/browser-ai/messages", (route) =>
    route.fulfill({ json: { code: 200, data: [] } }),
  );
  await open(page, "/assistant", "Assistant");
  await page.getByLabel("Message", { exact: true }).fill("Suggest a change");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(
    page.getByText("Review this change.", { exact: true }),
  ).toBeVisible();
  expect(executed).toBe(0);
  await page.getByRole("button", { name: "Run tool", exact: true }).click();
  expect(executed).toBe(0);
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(executed).toBe(1);
  await expect(page.getByRole("textbox", { name: "JSON data" })).toHaveValue(/confirmed/);
});
