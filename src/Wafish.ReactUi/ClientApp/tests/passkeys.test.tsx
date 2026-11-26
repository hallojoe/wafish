import { afterEach, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { config } from "@/api/client";
import {
  passkeysSupported,
  registerPasskey,
  signInWithPasskey,
} from "@/api/passkeys";
import { PasskeyPanel } from "@/components/passkeys";
import { AccountPage } from "@/pages/settings";
import { AuthProvider } from "@/components/auth";
import type { ReactNode } from "react";

const clients: QueryClient[] = [];
function mount(children: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  clients.push(client);
  return render(
    <QueryClientProvider client={client}>{children}</QueryClientProvider>,
  );
}
function fixture() {
  let registered = false,
    authenticated = false;
  const credential = {
    toJSON: () => ({ id: "device", rawId: "device", response: {} }),
  };
  const create = vi.fn().mockResolvedValue(credential);
  const get = vi.fn().mockResolvedValue(credential);
  class PublicKey {
    static parseCreationOptionsFromJSON(value: unknown) {
      return value;
    }
    static parseRequestOptionsFromJSON(value: unknown) {
      return value;
    }
    toJSON() {
      return {};
    }
  }
  vi.stubGlobal("PublicKeyCredential", PublicKey);
  vi.stubGlobal("navigator", { credentials: { create, get } });
  const fetch = vi.fn(async (input: string, _init?: RequestInit) => {
    let body: unknown = { code: 200 };
    if (input.endsWith("/options"))
      body = { id: "ceremony", publicKey: { challenge: "challenge" } };
    else if (input.endsWith("/registration")) registered = true;
    else if (input.endsWith("/revoke")) registered = false;
    else if (input.endsWith("/passkeys"))
      body = registered ? [{ id: "device", name: "Laptop" }] : [];
    else if (input.endsWith("/passkeys/login")) authenticated = true;
    else if (input.endsWith("/auth/status") && !authenticated)
      return new Response(JSON.stringify({ code: 401 }), { status: 401 });
    return new Response(JSON.stringify(body), { status: 200 });
  });
  vi.stubGlobal("fetch", fetch);
  return { create, get, fetch };
}
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  vi.unstubAllGlobals();
  config.passkeys = false;
});
it("uses native passkey JSON conversions and propagates cancellation signals", async () => {
  const { create, get, fetch } = fixture();
  const controller = new AbortController();
  expect(passkeysSupported()).toBe(true);
  await registerPasskey("Laptop", controller.signal);
  await signInWithPasskey(controller.signal);
  expect(create).toHaveBeenCalledWith({
    publicKey: { challenge: "challenge" },
    signal: controller.signal,
  });
  expect(get).toHaveBeenCalledWith({
    publicKey: { challenge: "challenge" },
    signal: controller.signal,
  });
  expect(
    fetch.mock.calls.every(([, init]) => init?.signal === controller.signal),
  ).toBe(true);
  const payload = JSON.parse(String(fetch.mock.calls[1][1]?.body));
  expect(payload).toMatchObject({
    id: "ceremony",
    credential: { id: "device" },
  });
});
it("keeps unsupported browsers and cancelled authenticators out of completion endpoints", async () => {
  const { create, get, fetch } = fixture();
  create.mockResolvedValue(null);
  get.mockResolvedValue(null);
  await expect(registerPasskey("Laptop")).rejects.toThrow("cancelled");
  await expect(signInWithPasskey()).rejects.toThrow("cancelled");
  expect(fetch).toHaveBeenCalledTimes(2);
  vi.stubGlobal("PublicKeyCredential", undefined);
  expect(passkeysSupported()).toBe(false);
  await expect(registerPasskey("Laptop")).rejects.toThrow("does not support");
  await expect(signInWithPasskey()).rejects.toThrow("does not support");
});
it("registers and revokes a named passkey through the account panel", async () => {
  const { fetch } = fixture();
  mount(<PasskeyPanel />);
  const register = screen.getByRole("button", { name: "Register passkey" });
  expect(register).toBeDisabled();
  fireEvent.change(screen.getByLabelText("Passkey name"), {
    target: { value: "Laptop" },
  });
  fireEvent.click(register);
  await screen.findByText("Laptop");
  fireEvent.click(screen.getByRole("button", { name: "Revoke", exact: true }));
  fireEvent.click(screen.getByRole("button", { name: "Confirm", exact: true }));
  await waitFor(() =>
    expect(fetch.mock.calls.some(([url]) => url.endsWith("/revoke"))).toBe(
      true,
    ),
  );
});
it("reports authenticator failures in the account panel", async () => {
  const { create } = fixture();
  create.mockRejectedValue(new Error("Authenticator unavailable"));
  mount(<PasskeyPanel />);
  fireEvent.change(screen.getByLabelText("Passkey name"), {
    target: { value: "Laptop" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Register passkey" }));
  await screen.findByText("Authenticator unavailable");
});
it("uses the passkey login control and retains recovery after a failure", async () => {
  const { get } = fixture();
  config.passkeys = true;
  get.mockRejectedValueOnce(new Error("Authenticator unavailable"));
  mount(
    <AuthProvider>
      <div>Authenticated workspace</div>
    </AuthProvider>,
  );
  const button = await screen.findByRole("button", {
    name: "Sign in with a passkey",
  });
  fireEvent.click(button);
  await screen.findByText("Authenticator unavailable");
  expect(screen.getByLabelText("Password", { exact: true })).toBeVisible();
  fireEvent.click(button);
  await screen.findByText("Authenticated workspace");
});

it("shows passkey management only when the JWT host enables it", async () => {
  fixture();
  config.passkeys = true;
  mount(<AccountPage />);
  expect(
    await screen.findByRole("heading", { name: "Passkeys" }),
  ).toBeVisible();
});
