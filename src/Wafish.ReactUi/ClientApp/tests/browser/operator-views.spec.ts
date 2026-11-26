import { test, expect } from "@playwright/test";

test("saved log views restore filters and columns after sign-out and sign-in", async ({
  page,
}) => {
  await page.goto("/console/observe/logs");
  const login = async () => {
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Logs", exact: true, level: 1 }),
    ).toBeVisible();
  };
  await login();
  await page.getByText("Saved views", { exact: true }).click();
  await page.getByLabel("Source", { exact: true }).selectOption("history");
  await page.getByLabel("Route", { exact: true }).fill("orders");
  await page.getByLabel("Minimum status", { exact: true }).fill("500");
  await page
    .getByRole("checkbox", { name: "Show Method", exact: true })
    .uncheck();
  await page
    .getByLabel("View name", { exact: true })
    .fill("Investigate failures");
  await page.getByRole("button", { name: "Save view", exact: true }).click();
  await expect(page.getByLabel("Saved view", { exact: true })).not.toHaveValue(
    "",
  );
  await page.getByRole("button", { name: "Sign out", exact: true }).click();
  await login();
  await page.getByText("Saved views", { exact: true }).click();
  await page
    .getByLabel("Saved view", { exact: true })
    .selectOption({ label: "Investigate failures" });
  await page.getByRole("button", { name: "Load view", exact: true }).click();
  await expect(page.getByLabel("Source", { exact: true })).toHaveValue(
    "history",
  );
  await expect(page.getByLabel("Route", { exact: true })).toHaveValue("orders");
  await expect(page.getByLabel("Minimum status", { exact: true })).toHaveValue(
    "500",
  );
  await expect(
    page.getByRole("checkbox", { name: "Show Method", exact: true }),
  ).not.toBeChecked();
  expect(
    await page.evaluate(() =>
      Object.values(localStorage).some((value) =>
        value.includes("Investigate failures"),
      ),
    ),
  ).toBe(false);
  await page.getByRole("button", { name: "Delete view", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(page.getByLabel("Saved view", { exact: true })).toHaveValue("");
});
