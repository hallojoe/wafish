import { test, expect } from "@playwright/test";

for (const [mode, port] of [["DefaultJwt", 5222], ["BasicAuth", 5223], ["ApiKey", 5224], ["DefaultJwt", 5225], ["BasicAuth", 5226], ["ApiKey", 5227]] as const) {
  test(`${mode} ${port >= 5225 ? "HTTPS" : "HTTP"} supports PathBase, nested prefixes, assets, API, and realtime`, async ({ page }) => {
    const outside: string[] = [];
    const errors: string[] = [];
    page.on("pageerror", error => errors.push(error.message));
    page.on("request", request => {
      const url = new URL(request.url());
      if (url.host === `127.0.0.1:${port}` && !url.pathname.startsWith("/edge/")) outside.push(url.pathname);
    });
    await page.goto(`${port >= 5225 ? "https" : "http"}://127.0.0.1:${port}/edge/manage/ui/configuration/routes`);
    if (mode !== "ApiKey") await page.getByLabel("Username", { exact: true }).fill("admin");
    await page.getByLabel(mode === "ApiKey" ? "API key" : "Password", { exact: true }).fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Routes", exact: true }).first()).toBeVisible();
    await page.getByRole("link", { name: "Overview", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Overview", exact: true })).toBeVisible();
    await expect(page.getByText("Connected", { exact: true })).toBeVisible();
    expect(new URL(page.url()).pathname).toBe("/edge/manage/ui");
    expect(errors).toEqual([]);
    expect(outside).toEqual([]);
  });
}
