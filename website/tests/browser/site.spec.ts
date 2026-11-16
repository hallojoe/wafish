import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

test("homepage explains the request, steps the cache, and links to onboarding", async ({
  page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.goto("/wafish/");
  await expect(
    page.getByRole("heading", { name: "An API gateway. Built for .NET." }),
  ).toBeVisible();
  await expect(
    page.getByRole("heading", { name: "What happens to a request?" }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Next: WAF" }).click();
  await expect(page.locator("[data-journey-title]")).toHaveText(
    "Inspect the request before it can continue.",
  );
  await page
    .getByRole("button", { name: "Step through: Public response cache" })
    .click();
  await expect(page.locator("[data-scene=cache] .scene-caption")).toContainText(
    "HIT",
  );
  await expect(page.locator("[data-scene=cache]")).toHaveAttribute(
    "data-renderer",
    "webgl",
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
test("request outcomes explain skipped stages and response origins", async ({
  page,
}) => {
  await page.goto("/wafish/");
  const journey = page.locator("[data-journey]");
  for (const example of [
    {
      label: "WAF blocks",
      stop: 1,
      status: "403 Forbidden",
      source: "WAF policy",
    },
    {
      label: "Limit reached",
      stop: 2,
      status: "429 Too Many Requests",
      source: "Rate-limit policy",
    },
    {
      label: "Cache hit",
      stop: 3,
      status: "200 OK",
      source: "Local response cache",
    },
  ]) {
    await journey
      .getByRole("button", { name: example.label, exact: true })
      .click();
    await expect(journey).toHaveAttribute("data-stage", String(example.stop));
    await expect(journey.locator('[data-state="skipped"]')).toHaveCount(
      4 - example.stop,
    );
    await expect(journey.locator('[data-journey-stage="4"]')).toBeDisabled();
    await journey.getByRole("button", { name: "Next: Response" }).click();
    await expect(journey.locator("[data-journey-progress]")).toHaveText(
      `${example.status} · 0 upstream requests · ${example.source}`,
    );
    await expect(journey.locator('[aria-current="step"]')).toHaveCount(1);
    await journey
      .getByRole("button", { name: "Back to previous request stage" })
      .click();
    await expect(journey).toHaveAttribute("data-stage", String(example.stop));
    await journey.getByRole("button", { name: "Next: Response" }).click();
    await journey.getByRole("button", { name: "Replay request" }).click();
    await expect(journey).toHaveAttribute("data-stage", "0");
  }
  await journey
    .getByRole("button", { name: "Forward to service", exact: true })
    .click();
  await expect(journey.locator('[data-state="skipped"]')).toHaveCount(0);
  for (let stage = 1; stage <= 5; stage++) {
    await journey.locator("[data-journey-advance]").click();
    await expect(journey).toHaveAttribute("data-stage", String(stage));
  }
  await expect(journey.locator("[data-journey-progress]")).toHaveText(
    "200 OK · 1 upstream request · Catalog service",
  );
  const route = journey.locator('[data-journey-stage="0"]');
  await route.focus();
  await page.keyboard.press("Enter");
  await expect(route).toHaveAttribute("aria-current", "step");
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
  await expect(page.locator("[data-journey] .journey-tile").first()).toHaveCSS(
    "transition-duration",
    "0s",
  );
  await expect(page.locator("[data-scene=cache] canvas")).toHaveCount(0);
  await page.getByRole("button", { name: "Next: WAF" }).click();
  await expect(page.locator("[data-journey]")).toHaveAttribute(
    "data-stage",
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
  await noGpu.locator("[data-scene=cache]").scrollIntoViewIfNeeded();
  await expect(noGpu.locator("[data-scene=cache]")).toHaveAttribute(
    "data-renderer",
    "static",
  );
  await expect(
    noGpu.locator("[data-scene=cache] .scene-fallback"),
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
