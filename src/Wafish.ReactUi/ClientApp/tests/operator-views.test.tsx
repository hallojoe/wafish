import { afterEach, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import { LogsPage } from "@/pages/observe";
import { OperatorViews, type SavedView } from "@/components/operator-views";
import type { ReactNode } from "react";
const clients: QueryClient[] = [];
function mount(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  clients.push(client);
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{node}</MemoryRouter>
    </QueryClientProvider>,
  );
}
function fixture(available = true) {
  let views: SavedView[] = [];
  let fail = false;
  const payloads: unknown[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      let data: unknown = [];
      if (input.includes("operator-views")) {
        if (init?.method === "DELETE") views = [];
        else if (init?.method === "POST" || init?.method === "PUT") {
          const body = JSON.parse(String(init.body));
          payloads.push(body);
          if (fail)
            return new Response(
              JSON.stringify({ code: 409, message: "Save failed" }),
              { status: 409 },
            );
          const view = { ...body, id: "view-1" };
          views = [view];
          data = view;
        } else data = { available, views };
      }
      return new Response(JSON.stringify({ code: 200, data }), { status: 200 });
    }),
  );
  return {
    payloads,
    fail: () => {
      fail = true;
    },
    recover: () => {
      fail = false;
    },
  };
}
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  vi.unstubAllGlobals();
});
it("saves and reloads log filters and columns without replacing failed drafts", async () => {
  const state = fixture();
  mount(<LogsPage />);
  fireEvent.click(screen.getByText("Saved views"));
  await screen.findByLabelText("View name");
  fireEvent.change(screen.getByLabelText("Source"), {
    target: { value: "history" },
  });
  fireEvent.change(screen.getByLabelText("Route", { exact: true }), {
    target: { value: "orders" },
  });
  fireEvent.change(screen.getByLabelText("Minimum status"), {
    target: { value: "500" },
  });
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Show Method", exact: true }),
  );
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "Errors" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save view" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Saved view")).toHaveValue("view-1"),
  );
  expect(state.payloads[0]).toMatchObject({
    name: "Errors",
    workspace: "logs",
    filters: { historical: true, route: "orders", minimumStatus: 500 },
  });
  expect(Object.keys(state.payloads[0] as object).sort()).toEqual([
    "columns",
    "filters",
    "name",
    "workspace",
  ]);
  fireEvent.change(screen.getByLabelText("Source"), {
    target: { value: "live" },
  });
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Show Method", exact: true }),
  );
  fireEvent.click(screen.getByRole("button", { name: "Load view" }));
  expect(screen.getByLabelText("Source")).toHaveValue("history");
  expect(screen.getByLabelText("Route", { exact: true })).toHaveValue("orders");
  expect(
    screen.getByRole("checkbox", { name: "Show Method", exact: true }),
  ).not.toBeChecked();
  state.fail();
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "Keep my draft" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save view" }));
  await screen.findByText("Save failed");
  expect(screen.getByLabelText("View name")).toHaveValue("Keep my draft");
  state.recover();
  fireEvent.click(screen.getByRole("button", { name: "Save view" }));
  await waitFor(() =>
    expect(
      screen.getByRole("option", { name: "Keep my draft" }),
    ).toBeInTheDocument(),
  );
  fireEvent.change(screen.getByLabelText("Saved view"), {
    target: { value: "" },
  });
  expect(screen.getByLabelText("View name")).toHaveValue("");
  fireEvent.change(screen.getByLabelText("Saved view"), {
    target: { value: "view-1" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Delete view" }));
  fireEvent.click(
    await screen.findByRole("button", { name: "Confirm", exact: true }),
  );
  await waitFor(() =>
    expect(screen.getByLabelText("Saved view")).toHaveValue(""),
  );
});
it("restores audit choices and protects the last visible column", async () => {
  fixture();
  mount(<LogsPage audit />);
  fireEvent.click(screen.getByText("Saved views"));
  await screen.findByLabelText("View name");
  fireEvent.change(screen.getByLabelText("Operation", { exact: true }), {
    target: { value: "Apply" },
  });
  for (const name of ["Description", "Account", "Time"])
    fireEvent.click(
      screen.getByRole("checkbox", { name: "Show " + name, exact: true }),
    );
  expect(
    screen.getByRole("checkbox", { name: "Show Operation", exact: true }),
  ).toBeDisabled();
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "Changes" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save view" }));
  await waitFor(() =>
    expect(screen.getByLabelText("Saved view")).toHaveValue("view-1"),
  );
  fireEvent.change(screen.getByLabelText("Operation", { exact: true }), {
    target: { value: "" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Load view" }));
  expect(screen.getByLabelText("Operation", { exact: true })).toHaveValue(
    "Apply",
  );
});
it("hides private preferences when no stable authenticated account exists", async () => {
  fixture(false);
  mount(
    <OperatorViews
      workspace="logs"
      filters={{
        historical: false,
        search: "",
        route: "",
        minimumStatus: null,
      }}
      columns={["timestamp"]}
      onColumns={() => {}}
      onApply={() => {}}
    />,
  );
  await waitFor(() =>
    expect(screen.queryByText("Saved views")).not.toBeInTheDocument(),
  );
});
