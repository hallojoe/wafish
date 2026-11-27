import { afterEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import type { ReactElement } from "react";
import { LocaleProvider } from "@/locales/i18n";
import { config } from "@/api/client";
import { ConfigurationPage, ImportPage } from "@/pages/configuration";
import { HistoryPage } from "@/pages/history";
import { BindingsPage, PluginsPage, PresetsPage } from "@/pages/plugins";
import {
  OverviewPage,
  TrafficPage,
  LogsPage,
  DestinationsPage,
  ResourcesPage,
} from "@/pages/observe";
import {
  SystemPage,
  NotificationsPage,
  AccountPage,
  IntegrationPage,
} from "@/pages/settings";
import { AssistantPage } from "@/pages/assistant";
vi.mock("@/hooks/live", () => ({
  useLive: (_hub: string, paused: boolean) =>
    paused ? "Paused" : "Disconnected",
}));
const clients: QueryClient[] = [];
function mount(page: ReactElement) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  clients.push(client);
  return {
    client,
    ...render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <LocaleProvider>{page}</LocaleProvider>
        </MemoryRouter>
      </QueryClientProvider>,
    ),
  };
}
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  vi.unstubAllGlobals();
  localStorage.clear();
  config.ai = true;
});
const pages: [string, () => ReactElement][] = [
  ["overview", () => <OverviewPage />],
  ["traffic", () => <TrafficPage />],
  ["logs", () => <LogsPage />],
  ["audit", () => <LogsPage audit />],
  ["destinations", () => <DestinationsPage />],
  ["resources", () => <ResourcesPage />],
  ["routes", () => <ConfigurationPage kind="routes" />],
  ["clusters", () => <ConfigurationPage kind="clusters" />],
  ["history", () => <HistoryPage />],
  ["plugins", () => <PluginsPage />],
  ["bindings", () => <BindingsPage />],
  ["presets", () => <PresetsPage />],
  ["system", () => <SystemPage />],
  ["notifications", () => <NotificationsPage />],
  ["account", () => <AccountPage />],
  ["integration", () => <IntegrationPage />],
  ["assistant", () => <AssistantPage />],
];
function deferred() {
  let resolve!: (response: Response) => void;
  const promise = new Promise<Response>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}
function failure() {
  return Response.json(
    {
      status: 503,
      outcome: "StorageFailed",
      message: "Storage unavailable",
      persistedVersion: 8,
      activeVersion: 7,
    },
    { status: 503 },
  );
}
describe.each(pages)("%s workspace", (_name, page) => {
  it("shows loading, a failed read, and successful recovery", async () => {
    const pending = deferred();
    const fetcher = vi
      .fn()
      .mockImplementation(() =>
        pending.promise.then((response) => response.clone()),
      );
    vi.stubGlobal("fetch", fetcher);
    const { client } = mount(page());
    expect(
      screen.getAllByRole("status", { name: "Loading" }).length,
    ).toBeGreaterThan(0);
    await act(async () => pending.resolve(failure()));
    expect((await screen.findAllByRole("alert"))[0]).toHaveTextContent(
      "Storage unavailable",
    );
    fetcher.mockImplementation(() =>
      Promise.resolve(Response.json({ items: [] })),
    );
    await act(async () => {
      await client.refetchQueries();
    });
    await waitFor(() => expect(screen.queryAllByRole("alert")).toHaveLength(0));
  });
  it("cancels obsolete reads when leaving the workspace", async () => {
    const pending = deferred();
    const signals: AbortSignal[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn((_path: string, init: RequestInit) => {
        signals.push(init.signal!);
        return pending.promise.then((response) => response.clone());
      }),
    );
    const view = mount(page());
    expect(signals.length).toBeGreaterThan(0);
    view.unmount();
    expect(signals.every((signal) => signal.aborted)).toBe(true);
    await act(async () =>
      pending.resolve(Response.json({ items: [{ name: "obsolete" }] })),
    );
    expect(screen.queryByText("obsolete")).not.toBeInTheDocument();
  });
});
describe("assistant common actions", () => {
  it.each([
    "List routes",
    "Inspect backends",
    "Review traffic",
    "List plugins",
    "Troubleshoot a route",
    "Create a route",
  ])("prepares an editable %s message without sending it", async (title) => {
    const fetcher = vi.fn<
      (path: string, init?: RequestInit) => Promise<Response>
    >(() => Promise.resolve(Response.json([])));
    vi.stubGlobal("fetch", fetcher);
    mount(<AssistantPage />);
    const suggestions = screen.getByRole("region", { name: "Common actions" });
    expect(within(suggestions).getAllByRole("button")).toHaveLength(6);
    fireEvent.click(
      within(suggestions).getByRole("button", {
        name: new RegExp(`^${title}`),
      }),
    );
    const input = screen.getByLabelText("Message");
    expect(input).toHaveFocus();
    expect((input as HTMLTextAreaElement).value.length).toBeGreaterThan(20);
    fireEvent.change(input, { target: { value: "My edited request" } });
    expect(input).toHaveValue("My edited request");
    expect(
      screen.getByRole("button", { name: "Send", exact: true }),
    ).toBeEnabled();
    expect(
      fetcher.mock.calls.every(
        ([, init]) => !init?.method || init.method === "GET",
      ),
    ).toBe(true);
  });

  it("preserves drafts and enables suggestions after clearing the message", () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(() => Promise.resolve(Response.json([]))),
    );
    mount(<AssistantPage />);
    const input = screen.getByLabelText("Message");
    fireEvent.change(input, { target: { value: "Keep my draft" } });
    const action = screen.getByRole("button", {
      name: /^List routes/,
    });
    expect(action).toBeDisabled();
    fireEvent.click(action);
    expect(input).toHaveValue("Keep my draft");
    fireEvent.change(input, { target: { value: "" } });
    expect(action).toBeEnabled();
    fireEvent.click(action);
    expect(input).toHaveValue(
      "List my routes with their matching paths, clusters, and enabled state.",
    );
  });

  it("disables suggestions throughout streaming and restores them afterward", async () => {
    let stream!: ReadableStreamDefaultController<Uint8Array>;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        stream = controller;
      },
    });
    vi.stubGlobal(
      "fetch",
      vi.fn((_path: string, init?: RequestInit) =>
        Promise.resolve(
          init?.method === "POST" ? new Response(body) : Response.json([]),
        ),
      ),
    );
    mount(<AssistantPage />);
    const action = screen.getByRole("button", {
      name: /^List routes/,
    });
    fireEvent.click(action);
    fireEvent.click(screen.getByRole("button", { name: "Send", exact: true }));
    await waitFor(() =>
      expect(screen.getByLabelText("Message")).toHaveValue(""),
    );
    expect(action).toBeDisabled();
    fireEvent.click(action);
    expect(screen.getByLabelText("Message")).toHaveValue("");
    await act(async () => stream.close());
    await waitFor(() => expect(action).toBeEnabled());
  });

  it("does not offer actions when AI is disabled", () => {
    config.ai = false;
    mount(<AssistantPage />);
    expect(
      screen.queryByRole("region", { name: "Common actions" }),
    ).not.toBeInTheDocument();
  });
});

describe("draft ownership", () => {
  function mockWrites() {
    const pending = deferred();
    vi.stubGlobal(
      "fetch",
      vi.fn((_path: string, init: RequestInit) =>
        init?.method && init.method !== "GET"
          ? pending.promise.then((response) => response.clone())
          : Promise.resolve(Response.json({ items: [] })),
      ),
    );
    return pending;
  }
  it.each(["routes", "clusters"] as const)(
    "retains a %s draft after a delayed failed save and refresh",
    async (kind) => {
      const pending = mockWrites();
      const { client } = mount(<ConfigurationPage kind={kind} />);
      fireEvent.click(
        screen.getByRole("button", {
          name: kind === "routes" ? "New route" : "New cluster",
          exact: true,
        }),
      );
      const dialog = await screen.findByRole("dialog");
      fireEvent.change(within(dialog).getByLabelText("Name", { exact: true }), {
        target: { value: "keep-this-draft" },
      });
      fireEvent.click(
        within(dialog).getByRole("button", { name: "Save changes" }),
      );
      await waitFor(() =>
        expect(
          within(dialog).getByRole("button", { name: "Save changes" }),
        ).toBeDisabled(),
      );
      await act(async () => {
        await client.refetchQueries();
      });
      expect(
        within(dialog).getByLabelText("Name", { exact: true }),
      ).toHaveValue("keep-this-draft");
      await act(async () => pending.resolve(failure()));
      expect(await within(dialog).findByRole("alert")).toHaveTextContent(
        "Storage unavailable",
      );
      expect(
        within(dialog).getByLabelText("Name", { exact: true }),
      ).toHaveValue("keep-this-draft");
    },
  );
  it.each([
    ["system", () => <SystemPage />, "Metadata retention days", "42"],
    ["notifications", () => <NotificationsPage />, "Cooldown seconds", "42"],
    ["integration", () => <IntegrationPage />, "Provider", "draft-provider"],
  ] as const)(
    "retains %s settings while failed writes invalidate queries",
    async (_name, page, label, value) => {
      const pending = mockWrites();
      const { client } = mount(page());
      const edit = (
        await screen.findAllByRole("button", { name: "Edit", exact: true })
      )[0];
      await waitFor(() => expect(edit).toBeEnabled());
      fireEvent.click(edit);
      const dialog = await screen.findByRole("dialog");
      fireEvent.change(within(dialog).getByLabelText(label), {
        target: { value },
      });
      fireEvent.click(
        within(dialog).getByRole("button", { name: "Save changes" }),
      );
      await act(async () => {
        await client.refetchQueries();
        pending.resolve(failure());
      });
      expect(await within(dialog).findByRole("alert")).toHaveTextContent(
        "Storage unavailable",
      );
      expect(within(dialog).getByLabelText(label)).toHaveValue(
        label === "Provider" ? value : Number(value),
      );
    },
  );
  it("preserves snapshot descriptions after failed creation", async () => {
    const pending = mockWrites();
    mount(<HistoryPage />);
    fireEvent.change(screen.getByLabelText("Snapshot description"), {
      target: { value: "deployment draft" },
    });
    fireEvent.click(
      screen.getByRole("button", { name: "Create snapshot", exact: true }),
    );
    await act(async () => pending.resolve(failure()));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Storage unavailable",
    );
    expect(screen.getByLabelText("Snapshot description")).toHaveValue(
      "deployment draft",
    );
  });
  it("preserves import JSON after failed validation", async () => {
    const pending = mockWrites();
    mount(<ImportPage />);
    const editor = screen.getByRole("textbox");
    const draft = '{"ReverseProxy":{"Routes":{"draft":{}},"Clusters":{}}}';
    fireEvent.change(editor, { target: { value: draft } });
    fireEvent.click(
      screen.getByRole("button", { name: "Validate", exact: true }),
    );
    await act(async () => pending.resolve(failure()));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Storage unavailable",
    );
    expect(editor).toHaveValue(draft);
  });
  it("preserves an unsent assistant message after transport rejection", async () => {
    const pending = mockWrites();
    mount(<AssistantPage />);
    fireEvent.change(screen.getByLabelText("Message"), {
      target: { value: "keep my request" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Send", exact: true }));
    expect(screen.getByRole("button", { name: "Stop" })).toBeInTheDocument();
    await act(async () => pending.resolve(failure()));
    expect(await screen.findByRole("alert")).toHaveTextContent("HTTP 503");
    expect(screen.getByLabelText("Message")).toHaveValue("keep my request");
  });
});

describe("stale query results", () => {
  it.each([false, true])(
    "keeps new filtered rows when an old %s query completes late",
    async (audit) => {
      const old = deferred();
      const fresh = deferred();
      const signals: AbortSignal[] = [];
      vi.stubGlobal(
        "fetch",
        vi.fn((path: string, init: RequestInit) => {
          signals.push(init.signal!);
          return (path.includes("needle") ? fresh : old).promise.then(
            (response) => response.clone(),
          );
        }),
      );
      mount(audit ? <LogsPage audit /> : <HistoryPage />);
      fireEvent.change(screen.getByLabelText(audit ? "Operation" : "Search"), {
        target: { value: "needle" },
      });
      await act(async () =>
        fresh.resolve(
          Response.json({
            items: [
              {
                versionId: "new",
                description: "new result",
                message: "new result",
              },
            ],
          }),
        ),
      );
      expect(await screen.findByText("new result")).toBeInTheDocument();
      expect(signals[0].aborted).toBe(true);
      await act(async () =>
        old.resolve(
          Response.json({
            items: [
              {
                versionId: "old",
                description: "old result",
                message: "old result",
              },
            ],
          }),
        ),
      );
      expect(screen.queryByText("old result")).not.toBeInTheDocument();
      expect(screen.getByText("new result")).toBeInTheDocument();
    },
  );
  it("shows assistant message errors and ignores obsolete session reads", async () => {
    const old = deferred();
    const fresh = deferred();
    const signal: AbortSignal[] = [];
    vi.stubGlobal(
      "fetch",
      vi.fn((path: string, init: RequestInit) => {
        if (path.endsWith("/api/ai/sessions"))
          return Promise.resolve(
            Response.json([
              { sessionId: "old", title: "Old session" },
              { sessionId: "new", title: "New selection" },
            ]),
          );
        signal.push(init.signal!);
        return (path.includes("/old/") ? old : fresh).promise.then((response) =>
          response.clone(),
        );
      }),
    );
    mount(<AssistantPage />);
    fireEvent.click(await screen.findByRole("button", { name: "Old session" }));
    expect(screen.getByRole("status", { name: "Loading" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "New selection" }));
    await act(async () => fresh.resolve(failure()));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Storage unavailable",
    );
    expect(signal[0].aborted).toBe(true);
    await act(async () =>
      old.resolve(
        Response.json([{ role: "assistant", content: "obsolete message" }]),
      ),
    );
    expect(screen.queryByText("obsolete message")).not.toBeInTheDocument();
  });
});

describe("extension and account drafts", () => {
  it.each(["bindings", "presets"])(
    "preserves %s input after a failed mutation",
    async (kind) => {
      const pending = deferred();
      vi.stubGlobal(
        "fetch",
        vi.fn((path: string, init: RequestInit) => {
          if (init?.method && init.method !== "GET")
            return pending.promise.then((response) => response.clone());
          return Promise.resolve(
            Response.json(
              path.endsWith("/plugins")
                ? [{ pluginId: "rate-limit", displayName: "Rate limit" }]
                : path.endsWith("/routes")
                  ? [{ routeId: "demo" }]
                  : [],
            ),
          );
        }),
      );
      mount(kind === "bindings" ? <BindingsPage /> : <PresetsPage />);
      await waitFor(() =>
        expect(
          screen.queryAllByRole("status", { name: "Loading" }),
        ).toHaveLength(0),
      );
      fireEvent.click(
        screen.getByRole("button", {
          name: kind === "bindings" ? "New binding" : "Save preset",
          exact: true,
        }),
      );
      const dialog = await screen.findByRole("dialog");
      const label = kind === "bindings" ? "Order" : "Name";
      fireEvent.change(within(dialog).getByLabelText(label, { exact: true }), {
        target: { value: kind === "bindings" ? "42" : "draft preset" },
      });
      if (kind === "bindings")
        fireEvent.change(within(dialog).getByLabelText("Target"), {
          target: { value: "demo" },
        });
      fireEvent.click(
        within(dialog).getByRole("button", { name: "Save changes" }),
      );
      await act(async () => pending.resolve(failure()));
      expect(await within(dialog).findByRole("alert")).toHaveTextContent(
        "Storage unavailable",
      );
      expect(within(dialog).getByLabelText(label, { exact: true })).toHaveValue(
        kind === "bindings" ? 42 : "draft preset",
      );
    },
  );
  it("keeps the verification code after a rejected two-factor change", async () => {
    const pending = deferred();
    vi.stubGlobal(
      "fetch",
      vi.fn((_path: string, init: RequestInit) =>
        init?.method === "POST"
          ? pending.promise.then((response) => response.clone())
          : Promise.resolve(
              Response.json({ secret: "fixture-secret", enabled: false }),
            ),
      ),
    );
    mount(<AccountPage />);
    fireEvent.click(await screen.findByRole("button", { name: "Set up" }));
    fireEvent.change(await screen.findByLabelText("Verification code"), {
      target: { value: "123456" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Verify and enable" }));
    await act(async () => pending.resolve(failure()));
    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Storage unavailable",
    );
    expect(screen.getByLabelText("Verification code")).toHaveValue("123456");
  });
});
