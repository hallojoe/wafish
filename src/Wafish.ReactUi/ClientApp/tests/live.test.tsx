import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import { OverviewPage } from "@/pages/observe";
const hub = vi.hoisted(() => ({
  handlers: new Map<string, (value?: unknown) => void>(),
  start: vi.fn(),
  stop: vi.fn(),
  close: () => {},
}));
vi.mock("@microsoft/signalr", () => ({
  HttpTransportType: { LongPolling: 1 },
  LogLevel: { None: 0 },
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    configureLogging() {
      return this;
    }
    build() {
      return {
        start: hub.start,
        stop: hub.stop,
        invoke: vi.fn(),
        on: (key: string, callback: (value?: unknown) => void) =>
          hub.handlers.set(key, callback),
        onreconnecting() {},
        onreconnected() {},
        onclose: (callback: () => void) => {
          hub.close = callback;
        },
      };
    }
  },
}));
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  hub.handlers.clear();
  vi.clearAllMocks();
});
it("applies overview events to the displayed locale query and releases the connection", async () => {
  hub.start.mockResolvedValue(undefined);
  hub.stop.mockResolvedValue(undefined);
  vi.stubGlobal(
    "fetch",
    vi.fn(() => Promise.resolve(Response.json({ routeCount: 123 }))),
  );
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  const view = render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <OverviewPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  expect(await screen.findByText("123")).toBeInTheDocument();
  await waitFor(() =>
    expect(screen.getByText("Connected")).toBeInTheDocument(),
  );
  await act(async () =>
    hub.handlers.get("OverviewUpdate")!({ routeCount: 987 }),
  );
  expect(await screen.findByText("987")).toBeInTheDocument();
  expect(client.getQueryData(["api/overview/snapshot", "en-US"])).toEqual({
    routeCount: 987,
  });
  act(() => hub.close());
  expect(screen.getByText("Disconnected")).toBeInTheDocument();
  view.unmount();
  expect(hub.stop).toHaveBeenCalledOnce();
  client.clear();
});
