import { test, expect } from "@playwright/test";
import { createServer } from "node:http";

test("aggregation editor selects named routes and serves combined JSON", async ({
  page,
}) => {
  const backend = createServer((request, response) => {
    response.setHeader("Content-Type", "application/json");
    response.end(JSON.stringify({ path: request.url }));
  });
  await new Promise<void>((resolve) => backend.listen(0, "127.0.0.1", resolve));
  const address = backend.address();
  if (!address || typeof address === "string")
    throw new Error("Backend address unavailable");
  const clusterId = "browser-aggregate-backend",
    child = "browser-aggregate-child",
    parent = "browser-aggregate-parent";
  try {
    await page.goto("/console");
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Overview", exact: true }),
    ).toBeVisible();
    expect(
      (
        await page.request.put(`/wafish/api/config/clusters/${clusterId}`, {
          data: {
            Destinations: {
              one: { Address: `http://127.0.0.1:${address.port}` },
            },
          },
        })
      ).ok(),
    ).toBeTruthy();
    expect(
      (
        await page.request.put(`/wafish/api/config/routes/${child}`, {
          data: {
            ClusterId: clusterId,
            Match: { Path: "/browser-child/{id}", Methods: ["GET"] },
          },
        })
      ).ok(),
    ).toBeTruthy();
    expect(
      (
        await page.request.put(`/wafish/api/config/routes/${parent}`, {
          data: { Match: { Path: "/browser-combined/{id}", Methods: ["GET"] } },
        })
      ).ok(),
    ).toBeTruthy();
    await page.goto("/console/extensions/bindings");
    await page
      .getByRole("button", { name: "New binding", exact: true })
      .click();
    const dialog = page.getByRole("dialog");
    await dialog
      .getByLabel("Plugin", { exact: true })
      .selectOption("request-aggregation");
    await dialog.getByLabel("Target", { exact: true }).selectOption(parent);
    await dialog.getByText("Common fields", { exact: true }).click();
    await dialog
      .getByRole("button", { name: "Add children", exact: true })
      .click();
    await dialog.getByLabel("name", { exact: true }).fill("profile");
    await dialog
      .getByLabel("Child route", { exact: true })
      .selectOption({ label: child });
    await dialog
      .getByLabel("Child path", { exact: true })
      .fill("/browser-child/{route.id}");
    await expect(dialog.getByRole("status")).toContainText("Referenced routes");
    await dialog
      .getByRole("button", { name: "Save changes", exact: true })
      .click();
    await expect(dialog).toHaveCount(0);
    await expect
      .poll(async () =>
        (await page.request.get("/browser-combined/42")).status(),
      )
      .toBe(200);
    const combined = await page.request.get("/browser-combined/42");
    expect(await combined.json()).toEqual({
      profile: { path: "/browser-child/42" },
    });
    expect(combined.headers()["cache-control"]).toBe("no-store");
    const removed = await page.request.delete(
      `/wafish/api/config/routes/${child}`,
    );
    expect(removed.ok()).toBeFalsy();
  } finally {
    const bindings = await page.request
      .get("/wafish/api/plugin-bindings")
      .then((response) => response.json())
      .catch(() => ({ data: [] }));
    for (const binding of bindings.data ?? [])
      if (binding.scopeId === parent)
        await page.request
          .delete(`/wafish/api/plugin-bindings/${binding.id}`)
          .catch(() => {});
    await page.request
      .delete(`/wafish/api/config/routes/${parent}`)
      .catch(() => {});
    await page.request
      .delete(`/wafish/api/config/routes/${child}`)
      .catch(() => {});
    await page.request
      .delete(`/wafish/api/config/clusters/${clusterId}`)
      .catch(() => {});
    backend.closeAllConnections();
    await new Promise<void>((resolve, reject) =>
      backend.close((error) => (error ? reject(error) : resolve())),
    );
  }
});
