import { test, expect } from "@playwright/test";

test("claims and access-rule editors persist structured mappings", async ({
  page,
}) => {
  await page.goto("/console");
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill(process.env.UI_TEST_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Overview", exact: true }),
  ).toBeVisible();
  const routeId = "browser-claims-access";
  expect(
    (
      await page.request.put(`/wafish/api/config/routes/${routeId}`, {
        data: { Match: { Path: "/browser-claims-access" } },
      })
    ).ok(),
  ).toBeTruthy();
  try {
    for (const plugin of ["claims-transform", "route-authorization"]) {
      await page.goto("/console/extensions/bindings");
      await page
        .getByRole("button", { name: "New binding", exact: true })
        .click();
      const dialog = page.getByRole("dialog");
      await dialog.getByLabel("Plugin", { exact: true }).selectOption(plugin);
      await dialog.getByLabel("Target", { exact: true }).selectOption(routeId);
      await dialog.getByText("Common fields", { exact: true }).click();
      if (plugin === "claims-transform") {
        await dialog
          .getByRole("button", { name: "Add mappings", exact: true })
          .click();
        await dialog.getByLabel("claim", { exact: true }).fill("sub");
        await dialog.getByLabel("name", { exact: true }).fill("X-User");
      } else {
        await dialog.getByLabel(/allowed scopes/i).fill("orders.read");
        await dialog
          .getByRole("button", { name: "Add claims", exact: true })
          .click();
        await dialog.getByLabel("type", { exact: true }).fill("tenant_id");
        await dialog.getByLabel(/allowed values/i).fill("alpha");
      }
      await dialog
        .getByRole("button", { name: "Save changes", exact: true })
        .click();
      await expect(dialog).toHaveCount(0);
    }
    const result = await (
      await page.request.get("/wafish/api/plugin-bindings")
    ).json();
    const bindings = result.data.filter(
      (b: { scopeId: string }) => b.scopeId === routeId,
    );
    expect(bindings).toHaveLength(2);
    const claims = JSON.parse(
      bindings.find(
        (b: { pluginId: string }) => b.pluginId === "claims-transform",
      ).configJson,
    );
    expect(claims.mappings[0]).toMatchObject({
      claim: "sub",
      name: "X-User",
      target: "Header",
      required: true,
    });
    const access = JSON.parse(
      bindings.find(
        (b: { pluginId: string }) => b.pluginId === "route-authorization",
      ).configJson,
    );
    expect(access.allowedScopes).toEqual(["orders.read"]);
    expect(access.claims).toEqual([
      { type: "tenant_id", allowedValues: ["alpha"] },
    ]);
  } finally {
    await page.request.delete(`/wafish/api/config/routes/${routeId}`).catch(() => {});
  }
});
