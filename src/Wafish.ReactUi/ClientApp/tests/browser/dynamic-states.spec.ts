import { expect, test } from "@playwright/test";
import type { Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
async function login(page: Page, base = "http://127.0.0.1:5219/console") {
  await page.goto(base);
  await page.getByLabel("Username", { exact: true }).fill("admin");
  await page
    .getByLabel("Password", { exact: true })
    .fill(process.env.UI_TEST_PASSWORD!);
  await page.getByRole("button", { name: "Sign in", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Overview", exact: true }),
  ).toBeVisible();
}
async function accessible(page: Page) {
  await page
    .getByRole("dialog")
    .evaluateAll((elements) =>
      Promise.all(
        elements.flatMap((element) =>
          element
            .getAnimations({ subtree: true })
            .map((animation) => animation.finished),
        ),
      ),
    );
  expect(
    (
      await new AxeBuilder({ page })
        .withTags(["wcag2a", "wcag2aa", "wcag21aa"])
        .analyze()
    ).violations,
  ).toEqual([]);
}
test("validation dialogs retain keyboard focus and expose errors accessibly", async ({
  page,
}) => {
  await login(page);
  await page.goto("/console/configuration/routes");
  const trigger = page.getByRole("button", { name: "New route", exact: true });
  await trigger.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await accessible(page);
  await dialog.getByRole("button", { name: "Save changes" }).focus();
  await page.keyboard.press("Enter");
  await expect(dialog.getByRole("alert")).toContainText("Required");
  await accessible(page);
  for (let index = 0; index < 15; index++) {
    await page.keyboard.press("Tab");
    expect(
      await dialog.evaluate((element) =>
        element.contains(document.activeElement),
      ),
    ).toBe(true);
  }
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
});
test("disconnected realtime views and mobile navigation remain keyboard accessible", async ({
  page,
}) => {
  await page.route("**/aneiang/hubs/**", (route) => route.abort("failed"));
  await login(page);
  await expect(
    page.getByRole("status").filter({ hasText: "Disconnected" }),
  ).toBeVisible();
  await accessible(page);
  await page.getByRole("button", { name: "Pause", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(
    page.getByRole("status").filter({ hasText: "Paused" }),
  ).toBeVisible();
  await page
    .getByRole("button", { name: "Resume", exact: true })
    .press("Enter");
  await expect(
    page.getByRole("status").filter({ hasText: "Disconnected" }),
  ).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  const trigger = page.getByRole("button", { name: "Open navigation" });
  await trigger.focus();
  await page.keyboard.press("Enter");
  const dialog = page.getByRole("dialog");
  await expect(dialog).toBeVisible();
  await accessible(page);
  for (let index = 0; index < 10; index++) {
    await page.keyboard.press("Tab");
    expect(
      await dialog.evaluate((element) =>
        element.contains(document.activeElement),
      ),
    ).toBe(true);
  }
  await page.keyboard.press("Escape");
  await expect(dialog).toHaveCount(0);
  await expect(trigger).toBeFocused();
  await page.keyboard.press("Enter");
  await dialog
    .getByRole("link", { name: "Configuration", exact: true })
    .focus();
  await page.keyboard.press("Enter");
  await expect(dialog).toHaveCount(0);
  await expect(
    page.getByRole("heading", { name: "Routes", exact: true }).first(),
  ).toBeVisible();
});
test("selectable large and plugin profiles expose seeded records", async ({
  page,
}) => {
  await login(page, "https://127.0.0.1:5228/edge/manage/ui");
  await page.goto("https://127.0.0.1:5228/edge/manage/ui/configuration/routes");
  await expect(page.getByRole("row")).toHaveCount(26);
  for (let index = 0; index < 10; index++)
    await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(
    page.getByRole("cell", { name: "profile-249", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("row")).toHaveCount(2);
  await page.getByLabel("Filter records").fill("profile-000");
  await expect(
    page.getByRole("cell", { name: "profile-000", exact: true }),
  ).toBeVisible();
  await login(page, "https://127.0.0.1:5229/edge/manage/ui");
  await page.goto("https://127.0.0.1:5229/edge/manage/ui/extensions/bindings");
  await expect(
    page.getByRole("cell", { name: "rate-limit", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("cell", { name: "compression", exact: true }),
  ).toBeVisible();
  await expect(page.getByRole("row")).toHaveCount(3);
});
for (const [index, outcome, status] of [
  [0, "StorageFailed", 503],
  [1, "VersionConflict", 409],
  [2, "StorageFailed", 503],
  [3, "ActivationFailed", 500],
] as const) {
  test(`real fixture failure ${index} exposes ${outcome} and preserves drafts`, async ({
    page,
  }) => {
    const origin = `https://127.0.0.1:${5230 + index}`;
    await login(page, `${origin}/edge/manage/ui`);
    await page.goto(`${origin}/edge/manage/ui/configuration/routes`);
    await page.getByRole("button", { name: "New route", exact: true }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Name", { exact: true }).fill("failure-draft");
    const response = page.waitForResponse(
      (result) =>
        result.url().endsWith("/api/config/routes/failure-draft") &&
        result.request().method() === "PUT",
    );
    await dialog.getByRole("button", { name: "Save changes" }).click();
    if (index === 2)
      await expect(
        dialog.getByRole("button", { name: "Save changes" }),
      ).toBeDisabled();
    const received = await response;
    expect(received.status()).toBe(status);
    const envelope = await received.json();
    expect(envelope.outcome).toBe(outcome);
    expect(typeof envelope.persistedVersion).toBe("number");
    expect(typeof envelope.activeVersion).toBe("number");
    if (index === 3)
      expect(envelope.persistedVersion).toBeGreaterThan(envelope.activeVersion);
    await expect(dialog.getByRole("alert")).toContainText(outcome);
    await expect(dialog.getByLabel("Name", { exact: true })).toHaveValue(
      "failure-draft",
    );
  });
}
