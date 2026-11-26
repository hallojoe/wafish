import { test, expect } from "@playwright/test";

test("passkeys enroll, sign in, revoke sessions, and retain password recovery", async ({
  page,
  context,
}) => {
  const origin = "https://localhost:5234";
  const ui = origin + "/edge/manage/ui";
  const client = await context.newCDPSession(page);
  await client.send("WebAuthn.enable");
  await client.send("WebAuthn.addVirtualAuthenticator", {
    options: {
      protocol: "ctap2",
      transport: "internal",
      hasResidentKey: true,
      hasUserVerification: true,
      isUserVerified: true,
      automaticPresenceSimulation: true,
    },
  });
  await page.goto(ui + "/settings/account");
  const passwordLogin = async () => {
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Account", exact: true }),
    ).toBeVisible();
  };
  await passwordLogin();
  await page
    .getByLabel("Passkey name", { exact: true })
    .fill("Browser authenticator");
  await page
    .getByRole("button", { name: "Register passkey", exact: true })
    .click();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Browser authenticator" }),
  ).toBeVisible();
  await page.request.post(origin + "/edge/manage/api/logout");
  await page.reload();
  await page
    .getByRole("button", { name: "Sign in with a passkey", exact: true })
    .click();
  await expect(
    page.getByRole("heading", { name: "Account", exact: true }),
  ).toBeVisible();
  const cookie = (await context.cookies()).find(
    (cookie) => cookie.name === "dashboard_token",
  );
  expect(cookie?.secure && cookie.httpOnly).toBe(true);
  await page.getByRole("button", { name: "Revoke", exact: true }).click();
  await page
    .getByRole("dialog")
    .getByRole("button", { name: "Confirm", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Sign in", exact: true }),
  ).toBeVisible();
  await passwordLogin();
  await expect(
    page.getByRole("listitem").filter({ hasText: "Browser authenticator" }),
  ).toHaveCount(0);
  expect(
    await page.evaluate(() =>
      Object.keys(localStorage).filter((key) =>
        /passkey|token|credential/i.test(key),
      ),
    ),
  ).toEqual([]);
});
