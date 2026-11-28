import { test, expect } from "@playwright/test";

test("authenticator enrollment, passwordless login, revocation, and recovery preserve nested paths", async ({
  page,
  context,
}) => {
  const session = await context.newCDPSession(page);
  await session.send("WebAuthn.enable");
  const { authenticatorId } = await session.send(
    "WebAuthn.addVirtualAuthenticator",
    {
      options: {
        protocol: "ctap2",
        transport: "internal",
        hasResidentKey: true,
        hasUserVerification: true,
        isUserVerified: true,
        automaticPresenceSimulation: true,
      },
    },
  );
  try {
    await page.goto("https://localhost:5234/edge/manage/ui/settings/account");
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByRole("heading", { name: "Account", exact: true }),
    ).toBeVisible();
    await page
      .getByLabel("Current password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page
      .getByLabel("Authenticator name", { exact: true })
      .fill("Browser test key");
    await page
      .getByRole("button", { name: "Add authenticator", exact: true })
      .click();
    await expect(
      page.getByRole("heading", { name: "Sign in", exact: true }),
    ).toBeVisible();
    const enrolled = await session.send("WebAuthn.getCredentials", {
      authenticatorId,
    });
    expect(enrolled.credentials).toHaveLength(1);
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByRole("button", {
        name: "Sign in with an authenticator",
        exact: true,
      })
      .click();
    await expect(
      page.getByRole("heading", { name: "Account", exact: true }),
    ).toBeVisible();
    await expect(
      page.getByText("Browser test key", { exact: true }),
    ).toBeVisible();
    const cookies = await context.cookies();
    expect(
      cookies.find((cookie) => cookie.name === "dashboard_token"),
    ).toMatchObject({ httpOnly: true, secure: true, sameSite: "Lax" });
    expect(
      cookies.find((cookie) => cookie.name === "wafish_webauthn"),
    ).toBeUndefined();
    await page
      .getByLabel("Current password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page
      .getByRole("button", { name: "Remove Browser test key", exact: true })
      .click();
    await expect(
      page.getByRole("heading", { name: "Sign in", exact: true }),
    ).toBeVisible();
    await page.getByLabel("Username", { exact: true }).fill("admin");
    await page
      .getByRole("button", {
        name: "Sign in with an authenticator",
        exact: true,
      })
      .click();
    await expect(page.getByRole("alert")).toContainText("unavailable");
    await page
      .getByLabel("Password", { exact: true })
      .fill(process.env.UI_TEST_PASSWORD!);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await expect(
      page.getByText("No authenticators registered.", { exact: true }),
    ).toBeVisible();
  } finally {
    await session.send("WebAuthn.removeVirtualAuthenticator", {
      authenticatorId,
    });
    await session.detach();
  }
});
