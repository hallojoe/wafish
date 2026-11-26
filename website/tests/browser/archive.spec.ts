import { test, expect } from "@playwright/test";

test("release route renders only its archived record and preserves navigation", async ({ page }) => {
  const response = await page.goto("/wafish/docs/release-notes/");
  expect(response?.status()).toBe(200);
  const article = page.locator("article.docs-content");
  await expect(article.getByRole("heading", { level: 1 })).toHaveText("1.0.0-rc.1 release notes");
  await expect(article).toContainText("Prepared: 2026-11-15");
  await expect(article).not.toContainText("Source inventory");
  await expect(article).not.toContainText("Wafish remaining work");
  await expect(article.getByRole("link", { name: "security boundaries" })).toHaveAttribute("href", "/wafish/docs/security/");
  await expect(article.getByRole("link", { name: "candidate evidence" })).toHaveAttribute(
    "href", "https://github.com/hallojoe/wafish/blob/main/ARCHIVE.md#archive-tests-wafish-regression-evidence-release-candidate-readme-md",
  );
  await expect(article.locator(".doc-source a")).toHaveAttribute(
    "href", "https://github.com/hallojoe/wafish/blob/main/ARCHIVE.md#archive-docs-release-notes-1-0-0-rc-1-md",
  );
  await page.locator(".docs-toc").getByRole("link", { name: "Verification", exact: true }).click();
  await expect(page).toHaveURL(/#verification$/);
  await expect(article.locator("#verification")).toBeVisible();
});
