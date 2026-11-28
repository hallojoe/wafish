import { test, expect } from "@playwright/test";
import type { Page } from "@playwright/test";
import { randomUUID } from "node:crypto";
import AxeBuilder from "@axe-core/playwright";

async function signIn(page: Page, path = "/configuration/routes") {
  await page.goto("/console" + path);
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill(process.env.UI_TEST_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(page.getByLabel("View name", { exact: true })).toBeVisible();
}
async function removeViews(page: Page, workspace: string, name: string) {
  const result = await page.request.get(
    `/wafish/api/operator-views/${workspace}`,
  );
  if (!result.ok()) return;
  const views = (await result.json()).data.views as {
    id: string;
    name: string;
    revision: number;
  }[];
  for (const view of views.filter((item) => item.name === name)) {
    const deleted = await page.request.delete(
      `/wafish/api/operator-views/${workspace}/${view.id}?expectedRevision=${view.revision}`,
      { headers: { "X-Wafish-Operator-View": "1" } },
    );
    expect(deleted.ok()).toBe(true);
  }
}
test("named route views restore after sign-out without browser credential storage", async ({
  page,
}, testInfo) => {
  const name = "Browser view " + randomUUID();
  await signIn(page);
  try {
    await page.getByLabel("Filter records", { exact: true }).fill("demo");
    await page.getByText("Visible columns", { exact: true }).click();
    await page
      .getByRole("checkbox", { name: "Show column: Path", exact: true })
      .uncheck();
    await page.getByLabel("View name", { exact: true }).fill(name);
    await page
      .getByRole("button", { name: "Save new view", exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "Update view", exact: true }),
    ).toBeEnabled();
    const saved = (
      await (await page.request.get("/wafish/api/operator-views/routes")).json()
    ).data.views.find((view: { name: string }) => view.name === name);
    expect(saved.settings.search).toBe("demo");
    expect(saved.settings.columns).not.toContain("match");
    await page.getByRole("button", { name: "Sign out", exact: true }).click();
    await expect(
      page.getByRole("button", { name: "Sign in", exact: true }),
    ).toBeVisible();
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByLabel("Filter records", { exact: true }),
    ).toHaveValue("demo");
    await expect(page.getByLabel("View name", { exact: true })).toHaveValue(
      name,
    );
    await expect(
      page.getByRole("columnheader", { name: "Path", exact: true }),
    ).toHaveCount(0);
    await page.reload();
    await expect(
      page.getByLabel("Filter records", { exact: true }),
    ).toHaveValue("demo");
    const storage = await page.evaluate(() =>
      JSON.stringify({
        local: { ...localStorage },
        session: { ...sessionStorage },
      }),
    );
    expect(storage).not.toContain(name);
    expect(storage).not.toContain(process.env.UI_TEST_PASSWORD!);
    await page.getByText("Visible columns", { exact: true }).click();
    const accessibility = await new AxeBuilder({ page })
      .include('section[aria-label="Saved views"]')
      .analyze();
    expect(accessibility.violations).toEqual([]);
    await page.screenshot({
      path: testInfo.outputPath("operator-views.png"),
      fullPage: true,
    });
  } finally {
    await removeViews(page, "routes", name);
  }
});
test("saved log views restore server filters and preserve drafts after a rejected update", async ({
  page,
}) => {
  const name = "Browser logs " + randomUUID();
  await signIn(page, "/observe/logs");
  try {
    await page.getByLabel("Source", { exact: true }).selectOption("history");
    await page.getByLabel("Search", { exact: true }).fill("synthetic");
    await page.getByLabel("Route", { exact: true }).fill("demo");
    await page.getByLabel("Minimum status", { exact: true }).fill("400");
    await page.getByLabel("View name", { exact: true }).fill(name);
    await page
      .getByRole("button", { name: "Save new view", exact: true })
      .click();
    await expect(
      page.getByRole("button", { name: "Update view", exact: true }),
    ).toBeEnabled();
    await page.reload();
    await expect(page.getByLabel("Source", { exact: true })).toHaveValue(
      "history",
    );
    await expect(page.getByLabel("Search", { exact: true })).toHaveValue(
      "synthetic",
    );
    await expect(page.getByLabel("Route", { exact: true })).toHaveValue("demo");
    await expect(
      page.getByLabel("Minimum status", { exact: true }),
    ).toHaveValue("400");
    await page.route("**/api/operator-views/logs/*", (route) =>
      route.request().method() === "PUT"
        ? route.fulfill({
            status: 409,
            contentType: "application/json",
            body: JSON.stringify({
              message: "Fixture revision conflict",
              success: false,
            }),
          })
        : route.continue(),
    );
    await page.getByLabel("Search", { exact: true }).fill("retained draft");
    await page
      .getByRole("button", { name: "Update view", exact: true })
      .click();
    await expect(page.getByRole("alert")).toContainText(
      "Fixture revision conflict",
    );
    await expect(page.getByLabel("Search", { exact: true })).toHaveValue(
      "retained draft",
    );
    await page
      .getByRole("button", { name: "Reload saved views", exact: true })
      .click();
    await expect(page.getByRole("alert")).toHaveCount(0);
    await expect(page.getByLabel("Search", { exact: true })).toHaveValue(
      "retained draft",
    );
    await page.unroute("**/api/operator-views/logs/*");
  } finally {
    await removeViews(page, "logs", name);
  }
});
