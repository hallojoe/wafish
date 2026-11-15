import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("homepage renders the instrument, steps the cache, and links to onboarding", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/wafish/");
  await expect(
    page.getByRole("heading", { name: "An API gateway. Built for .NET." }),
  ).toBeVisible();
  await expect(page.locator("[data-scene=request]")).toHaveAttribute(
    "data-renderer",
    "webgl",
  );
  await page
    .getByRole("button", { name: "Step through: Public response cache" })
    .click();
  await expect(page.locator("[data-scene=cache] .scene-caption")).toContainText(
    "HIT",
  );
  await page
    .getByRole("button", { name: "Replay Public response cache" })
    .click();
  await expect(page.locator("[data-scene=cache]")).toHaveAttribute(
    "data-step",
    "0",
  );
  await page.getByRole("link", { name: "Build your gateway" }).click();
  await expect(
    page.getByRole("heading", { name: "Your first gateway", exact: true }),
  ).toBeVisible();
  expect(errors).toEqual([]);
});
test("documentation search and source links work below the project base", async ({
  page,
}) => {
  await page.goto("/wafish/docs/");
  await page.getByRole("button", { name: "Search documentation" }).click();
  await page.getByRole("searchbox").fill("lease");
  await expect(page.locator(".search-results li").first()).toBeVisible();
  expect(
    await page.locator(".search-results a").first().getAttribute("href"),
  ).toMatch(/^\/wafish\/docs\//);
  await page.locator(".search-results a").first().click();
  await expect(page.locator(".docs-content h1")).toBeVisible();
});
test("keyboard slides, replay, notes, and sound controls work", async ({
  page,
}) => {
  await page.goto("/wafish/presentation/");
  await expect(page.locator(".reveal.ready")).toBeVisible();
  await expect(page.locator(".slides>section")).toHaveCount(26);
  await expect(page.locator("body")).toHaveCSS(
    "background-color",
    "rgb(20, 25, 21)",
  );
  await page.keyboard.press("ArrowRight");
  await expect(page).toHaveURL(/#\/1$/);
  await page.goto("/wafish/presentation/#/6");
  await page.locator("section.present [data-scene-next]").click();
  await expect(page.locator("section.present .scene-caption")).toContainText(
    "Blocked",
  );
  await page.locator("section.present [data-scene-reset]").click();
  await expect(page.locator("section.present .scene-caption")).toContainText(
    "Allowed",
  );
  await page.getByRole("button", { name: "Sound off" }).click();
  await expect(page.getByRole("button", { name: "Sound on" })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  const popup = page.waitForEvent("popup");
  await page.getByRole("button", { name: "Speaker notes" }).click();
  const notes = await popup;
  await notes.waitForLoadState();
  await expect(notes.locator("body")).toContainText("Upcoming");
  await notes.close();
});
test("all slides fit within their 1440 by 900 canvas", async ({ page }) => {
  await page.goto("/wafish/presentation/");
  await expect(page.locator(".reveal.ready")).toBeVisible();
  for (let i = 0; i < 26; i++) {
    await page.evaluate((index) => {
      location.hash = "#/" + index;
    }, i);
    await expect(
      page.locator(`section.present[data-slide="${i}"]`),
    ).toBeVisible();
    const overflow = await page
      .locator("section.present .slide-inner")
      .evaluate((element) => ({
        height: element.scrollHeight - element.clientHeight,
        width: element.scrollWidth - element.clientWidth,
      }));
    expect(overflow.height, `slide ${i + 1} height`).toBeLessThanOrEqual(2);
    expect(overflow.width, `slide ${i + 1} width`).toBeLessThanOrEqual(2);
  }
});
test("reduced motion and unavailable WebGL retain readable demonstrations", async ({
  browser,
}) => {
  const context = await browser.newContext({ reducedMotion: "reduce" });
  const page = await context.newPage();
  await page.goto("/wafish/");
  await expect(page.locator("[data-scene=request] canvas")).toHaveCount(0);
  await page
    .getByRole("button", { name: "Step through: Request path" })
    .click();
  await expect(page.locator("[data-scene=request]")).toHaveAttribute(
    "data-step",
    "1",
  );
  await context.close();
  const fallback = await browser.newContext();
  await fallback.addInitScript(() => {
    const original = HTMLCanvasElement.prototype.getContext;
    HTMLCanvasElement.prototype.getContext = function (
      this: HTMLCanvasElement,
      type: string,
      ...args: unknown[]
    ) {
      if (/webgl/.test(type)) return null;
      return original.apply(this, [type, ...args] as never);
    } as typeof original;
  });
  const noGpu = await fallback.newPage();
  await noGpu.goto("/wafish/");
  await expect(noGpu.locator("[data-scene=request]")).toHaveAttribute(
    "data-renderer",
    "static",
  );
  await expect(
    noGpu.locator("[data-scene=request] .scene-fallback"),
  ).toBeVisible();
  await fallback.close();
});
test("mobile home and documentation have no horizontal overflow", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  for (const route of ["", "docs/getting-started/", "brand/"]) {
    await page.goto("/wafish/" + route);
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBeTruthy();
  }
});
test("home, docs, and brand pages have no serious accessibility violations", async ({
  page,
}) => {
  for (const route of [
    "",
    "docs/",
    "brand/",
    "presentation/",
    "presentation/#/6",
  ]) {
    await page.goto("/wafish/" + route);
    const results = await new AxeBuilder({ page }).analyze();
    expect(
      results.violations.filter(
        (item) => item.impact === "serious" || item.impact === "critical",
      ),
      route,
    ).toEqual([]);
  }
});
test("unknown routes return 404 with recovery links", async ({ page }) => {
  const response = await page.goto("/wafish/does-not-exist/");
  expect(response?.status()).toBe(404);
  await expect(
    page.getByRole("heading", { name: "This route ends here." }),
  ).toBeVisible();
});
