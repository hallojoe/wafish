import { afterEach, beforeEach, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { config, onSessionExpired } from "@/api/client";
import {
  authenticatorWrite,
  beginAuthenticatorLogin,
  completeAuthenticatorLogin,
  enrollAuthenticator,
  supportsWebAuthn,
} from "@/api/webauthn";
import { Authenticators } from "@/components/authenticators";
import { AuthProvider } from "@/components/auth";
import { LocaleProvider } from "@/locales/i18n";

const bytes = new Uint8Array([251, 255, 0]).buffer;
const id = "-_8A";
const assertion = {
  challengeId: "challenge",
  requiresTwoFactor: false,
  options: {
    challenge: id,
    rpId: "localhost",
    userVerification: "required" as const,
    allowCredentials: [{ id, type: "public-key" as const }],
  },
};
const creation = {
  challengeId: "challenge",
  options: {
    challenge: id,
    rp: { id: "localhost", name: "Test" },
    user: { id, name: "admin", displayName: "admin" },
    pubKeyCredParams: [{ type: "public-key", alg: -7 }],
    excludeCredentials: [{ id, type: "public-key" }],
  },
};
function credential(userHandle: ArrayBuffer | null = bytes) {
  return {
    id,
    rawId: bytes,
    type: "public-key",
    getClientExtensionResults: () => ({}),
    response: {
      clientDataJSON: bytes,
      attestationObject: bytes,
      getTransports: () => ["internal"],
      authenticatorData: bytes,
      signature: bytes,
      userHandle,
    },
  };
}
const browser = { create: vi.fn(), get: vi.fn() };
const originalConfig = { ...config };
beforeEach(() => {
  config.webAuthn = true;
  vi.stubGlobal("isSecureContext", true);
  vi.stubGlobal("PublicKeyCredential", class {});
  Object.defineProperty(navigator, "credentials", {
    configurable: true,
    value: browser,
  });
  browser.create.mockReset().mockResolvedValue(credential());
  browser.get.mockReset().mockResolvedValue(credential());
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  Object.assign(config, originalConfig);
  config.webAuthn = originalConfig.webAuthn;
  onSessionExpired(() => {});
});
function mount(child: React.ReactNode) {
  return render(
    <QueryClientProvider
      client={
        new QueryClient({
          defaultOptions: {
            queries: { retry: false },
            mutations: { retry: false },
          },
        })
      }
    >
      <LocaleProvider>{child}</LocaleProvider>
    </QueryClientProvider>,
  );
}
it("requires configuration, secure context, and browser support", () => {
  expect(supportsWebAuthn()).toBe(true);
  config.webAuthn = false;
  expect(supportsWebAuthn()).toBe(false);
  config.webAuthn = true;
  vi.stubGlobal("isSecureContext", false);
  expect(supportsWebAuthn()).toBe(false);
});
it("converts browser buffers and preserves cancellation without storing tokens", async () => {
  const signal = new AbortController().signal;
  const fetcher = vi
    .fn()
    .mockResolvedValueOnce(Response.json(creation))
    .mockResolvedValueOnce(Response.json({}));
  vi.stubGlobal("fetch", fetcher);
  await enrollAuthenticator("Laptop", "secret", "123456", signal);
  expect(browser.create).toHaveBeenCalledWith(
    expect.objectContaining({
      signal,
      publicKey: expect.objectContaining({
        challenge: bytes,
        user: expect.objectContaining({ id: bytes }),
        excludeCredentials: [{ id: bytes, type: "public-key" }],
      }),
    }),
  );
  expect(JSON.parse(fetcher.mock.calls[1][1].body)).toMatchObject({
    challengeId: "challenge",
    credential: {
      rawId: id,
      response: { attestationObject: id, transports: ["internal"] },
    },
  });
  fetcher
    .mockReset()
    .mockResolvedValue(Response.json({ token: "never-persist" }));
  await completeAuthenticatorLogin(assertion, "123456", signal);
  expect(browser.get).toHaveBeenCalledWith(
    expect.objectContaining({
      signal,
      publicKey: expect.objectContaining({
        challenge: bytes,
        allowCredentials: [{ id: bytes, type: "public-key" }],
      }),
    }),
  );
  expect(JSON.parse(fetcher.mock.calls[0][1].body)).toMatchObject({
    twoFactorCode: "123456",
    credential: { response: { userHandle: id, signature: id } },
  });
  expect(localStorage.getItem("token")).toBeNull();
  browser.get.mockResolvedValue(credential(null));
  await completeAuthenticatorLogin(assertion, "");
  expect(
    JSON.parse(fetcher.mock.calls[1][1].body).credential.response.userHandle,
  ).toBeNull();
});
it("reports browser cancellation without submitting a completion", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(Response.json(creation)));
  browser.create.mockResolvedValue(null);
  await expect(enrollAuthenticator("Key", "secret", "")).rejects.toThrow(
    "cancelled",
  );
  expect(fetch).toHaveBeenCalledTimes(1);
  browser.get.mockRejectedValue(
    new DOMException("Cancelled", "NotAllowedError"),
  );
  await expect(completeAuthenticatorLogin(assertion, "")).rejects.toThrow(
    "Cancelled",
  );
});
it("keeps valid sessions when fresh credential proof fails", async () => {
  const expired = vi.fn();
  onSessionExpired(expired);
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        Response.json(
          { message: "Current credentials are required." },
          { status: 401 },
        ),
      ),
  );
  await expect(
    authenticatorWrite("credentials/remove", {}),
  ).rejects.toMatchObject({ status: 401 });
  expect(expired).not.toHaveBeenCalled();
  await expect(beginAuthenticatorLogin("unknown")).rejects.toMatchObject({
    status: 401,
  });
});
it("preserves the enrollment name and clears secrets after failed proof", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn((url: string) =>
      Promise.resolve(
        url.endsWith("credentials")
          ? Response.json({ credentials: [] })
          : Response.json(
              { message: "Current credentials are required." },
              { status: 401 },
            ),
      ),
    ),
  );
  mount(<Authenticators requiresTwoFactor />);
  await screen.findByText("No authenticators registered.");
  fireEvent.change(screen.getByLabelText("Authenticator name"), {
    target: { value: "My laptop" },
  });
  fireEvent.change(screen.getByLabelText("Current password"), {
    target: { value: "wrong" },
  });
  expect(
    screen.getByRole("button", { name: "Add authenticator" }),
  ).toBeDisabled();
  fireEvent.change(screen.getByLabelText("Authenticator verification code"), {
    target: { value: "123456" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Add authenticator" }));
  await screen.findByText("Current credentials are required.");
  expect(screen.getByLabelText("Authenticator name")).toHaveValue("My laptop");
  expect(screen.getByLabelText("Current password")).toHaveValue("");
  expect(screen.getByLabelText("Authenticator verification code")).toHaveValue(
    "",
  );
  expect(browser.create).not.toHaveBeenCalled();
});
it("removes a selected credential with fresh proof", async () => {
  const fetcher = vi.fn((url: string) =>
    Promise.resolve(
      url.endsWith("credentials")
        ? Response.json({
            credentials: [
              {
                id: "selected-key",
                name: "Laptop",
                createdUtc: "2026-01-01T00:00:00Z",
                lastUsedUtc: "2026-01-02T00:00:00Z",
              },
            ],
          })
        : new Response(null, { status: 204 }),
    ),
  );
  vi.stubGlobal("fetch", fetcher);
  mount(<Authenticators requiresTwoFactor={false} />);
  await screen.findByText("Laptop");
  fireEvent.change(screen.getByLabelText("Current password"), {
    target: { value: "secret" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Remove Laptop" }));
  await waitFor(() =>
    expect(
      fetcher.mock.calls.some(([url]) => url.endsWith("credentials/remove")),
    ).toBe(true),
  );
  const call = (fetcher.mock.calls as unknown as [string, RequestInit][]).find(
    ([url]) => url.endsWith("credentials/remove"),
  )!;
  expect(JSON.parse(call[1].body as string)).toEqual({
    credentialId: "selected-key",
    password: "secret",
    twoFactorCode: null,
  });
});
it("shows an unsupported browser message while retaining credential management", async () => {
  vi.stubGlobal("isSecureContext", false);
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(Response.json({ credentials: [] })),
  );
  mount(<Authenticators requiresTwoFactor={false} />);
  await screen.findByText(
    "Authenticator enrollment requires a supported browser and a secure connection.",
  );
  expect(
    screen.queryByRole("button", { name: "Add authenticator" }),
  ).toBeNull();
});
it("authenticates without a password and prompts for configured TOTP before browser verification", async () => {
  let authenticated = false;
  const fetcher = vi.fn((url: string) => {
    if (url.endsWith("login/options"))
      return Promise.resolve(
        Response.json({ ...assertion, requiresTwoFactor: true }),
      );
    if (url.endsWith("login/complete")) {
      authenticated = true;
      return Promise.resolve(Response.json({}));
    }
    return Promise.resolve(
      Response.json({}, { status: authenticated ? 200 : 401 }),
    );
  });
  vi.stubGlobal("fetch", fetcher);
  mount(
    <AuthProvider>
      <p>Signed in workspace</p>
    </AuthProvider>,
  );
  const button = await screen.findByRole("button", {
    name: "Sign in with an authenticator",
  });
  fireEvent.click(button);
  await screen.findByText("Enter your username.");
  fireEvent.change(screen.getByLabelText("Username"), {
    target: { value: "admin" },
  });
  fireEvent.click(button);
  await screen.findByLabelText("Verification code");
  expect(browser.get).not.toHaveBeenCalled();
  fireEvent.change(screen.getByLabelText("Verification code"), {
    target: { value: "123456" },
  });
  fireEvent.click(button);
  await screen.findByText("Signed in workspace");
  expect(browser.get).toHaveBeenCalledOnce();
});
