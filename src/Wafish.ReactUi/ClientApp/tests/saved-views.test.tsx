import { afterEach, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { LocaleProvider } from "@/locales/i18n";
import { Records } from "@/components/workspace";
import { LogsPage } from "@/pages/observe";
import { setCredential } from "@/api/client";
import type { OperatorView, ViewSettings } from "@/api/operator-views";

const settings: ViewSettings = {
  search: "needle",
  filter: "",
  route: "",
  minimumStatus: null,
  historical: false,
  columns: ["routeId"],
};
const initial = (): OperatorView => ({
  id: "11111111-1111-1111-1111-111111111111",
  workspace: "routes",
  name: "Investigations",
  settings,
  isDefault: true,
  revision: 1,
});
const columns = [
  { key: "routeId", label: "Route" },
  { key: "statusCode", label: "Status" },
];
const clients: QueryClient[] = [];
function mount(
  node = (
    <Records
      viewKey="routes"
      data={[
        { routeId: "needle-one", statusCode: 503 },
        { routeId: "other", statusCode: 200 },
      ]}
      columns={columns}
    />
  ),
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  clients.push(client);
  return render(
    <QueryClientProvider client={client}>
      <LocaleProvider>{node}</LocaleProvider>
    </QueryClientProvider>,
  );
}
afterEach(() => {
  cleanup();
  clients.splice(0).forEach((client) => client.clear());
  vi.unstubAllGlobals();
  setCredential({});
  localStorage.clear();
});
function server(views: OperatorView[] = []) {
  const state = { views, fail: false };
  const fetcher = vi.fn(async (url: string, init?: RequestInit) => {
    if (!url.includes("operator-views"))
      return Response.json({
        data: {
          items: [
            { method: "POST", statusCode: 500, path: "/demo", routeId: "one" },
          ],
        },
      });
    if (!init?.method)
      return Response.json({ data: { available: true, views: state.views } });
    if (state.fail)
      return Response.json(
        { message: "The saved view changed.", success: false },
        { status: 409 },
      );
    const id = url.split("/").pop()!.split("?")[0];
    if (init.method === "DELETE") {
      state.views = state.views.filter((view) => view.id !== id);
      return Response.json({ code: 200, success: true });
    }
    const body = JSON.parse(init.body as string);
    const view: OperatorView = {
      id,
      workspace: "routes",
      name: body.name,
      settings: body.settings,
      isDefault: body.isDefault,
      revision: body.expectedRevision + 1,
    };
    state.views = [
      ...state.views
        .filter((old) => old.id !== id)
        .map((old) => ({ ...old, isDefault: false })),
      view,
    ];
    return Response.json({ data: view });
  });
  vi.stubGlobal("fetch", fetcher);
  return { state, fetcher };
}
it("saves named filters and columns, restores them, updates, selects, and deletes views", async () => {
  const { state, fetcher } = server();
  const first = mount();
  await screen.findByLabelText("View name");
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "needle" },
  });
  fireEvent.click(screen.getByText("Visible columns"));
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Show column: Status" }),
  );
  expect(
    screen.queryByRole("columnheader", { name: "Status" }),
  ).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "Investigations" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save new view" }));
  await waitFor(() => expect(state.views).toHaveLength(1));
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Update view" })).toBeEnabled(),
  );
  const created = state.views[0];
  expect(created.settings).toEqual(settings);
  const request = fetcher.mock.calls.find(
    ([, init]) => init?.method === "PUT",
  )![1]!;
  expect(request.headers).toMatchObject({ "X-Wafish-Operator-View": "1" });
  expect(Object.keys(JSON.parse(request.body as string)).sort()).toEqual([
    "expectedRevision",
    "isDefault",
    "name",
    "settings",
  ]);
  expect(
    Object.keys(localStorage).filter((key) =>
      /view|credential|token/i.test(key),
    ),
  ).toEqual([]);
  first.unmount();
  mount();
  await waitFor(() =>
    expect(screen.getByLabelText("Filter records")).toHaveValue("needle"),
  );
  expect(
    screen.queryByRole("columnheader", { name: "Status" }),
  ).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "other" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Update view" }));
  await waitFor(() => expect(state.views[0].revision).toBe(2));
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Save new view" })).toBeEnabled(),
  );
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "Everything" },
  });
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Save new view" }));
  await waitFor(() => expect(state.views).toHaveLength(2));
  await waitFor(() =>
    expect(screen.getByLabelText("Saved view")).toBeEnabled(),
  );
  fireEvent.change(screen.getByLabelText("Saved view"), {
    target: { value: created.id },
  });
  await waitFor(() =>
    expect(screen.getByLabelText("Filter records")).toHaveValue("other"),
  );
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Delete view" })).toBeEnabled(),
  );
  fireEvent.click(screen.getByRole("button", { name: "Delete view" }));
  await waitFor(() => expect(state.views).toHaveLength(1));
  await waitFor(() =>
    expect(screen.getByLabelText("View name")).toHaveValue(""),
  );
});
it("preserves filter and name drafts after conflicts and explicit reload", async () => {
  const { state } = server([initial()]);
  mount();
  await waitFor(() =>
    expect(screen.getByLabelText("View name")).toHaveValue("Investigations"),
  );
  fireEvent.change(screen.getByLabelText("View name"), {
    target: { value: "My draft" },
  });
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "unsaved" },
  });
  state.fail = true;
  fireEvent.click(screen.getByRole("button", { name: "Update view" }));
  await screen.findByRole("alert");
  expect(screen.getByLabelText("View name")).toHaveValue("My draft");
  expect(screen.getByLabelText("Filter records")).toHaveValue("unsaved");
  fireEvent.click(screen.getByRole("button", { name: "Reload saved views" }));
  await waitFor(() =>
    expect(screen.queryByRole("alert")).not.toBeInTheDocument(),
  );
  expect(screen.getByLabelText("View name")).toHaveValue("My draft");
  expect(screen.getByLabelText("Filter records")).toHaveValue("unsaved");
  state.fail = false;
  fireEvent.click(screen.getByRole("button", { name: "Update view" }));
  await waitFor(() => expect(state.views[0].name).toBe("My draft"));
});
it("does not overwrite typing while initial restoration is pending", async () => {
  let resolve!: (response: Response) => void;
  vi.stubGlobal(
    "fetch",
    vi.fn(
      () =>
        new Promise<Response>((done) => {
          resolve = done;
        }),
    ),
  );
  mount();
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "new draft" },
  });
  await act(async () =>
    resolve(Response.json({ data: { available: true, views: [initial()] } })),
  );
  expect(screen.getByLabelText("Filter records")).toHaveValue("new draft");
});
it("aborts late preference reads and writes when the authenticated workspace unmounts", async () => {
  let resolve!: (response: Response) => void;
  let signal: AbortSignal | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn((_url, init) => {
      signal = init.signal;
      return new Promise<Response>((done) => {
        resolve = done;
      });
    }),
  );
  const first = mount();
  first.unmount();
  expect(signal?.aborted).toBe(true);
  server([]);
  mount();
  await screen.findByLabelText("View name");
  await act(async () =>
    resolve(Response.json({ data: { available: true, views: [initial()] } })),
  );
  expect(screen.getByLabelText("View name")).toHaveValue("");
  cleanup();
  server([initial()]);
  const old = mount();
  await waitFor(() =>
    expect(screen.getByLabelText("View name")).toHaveValue("Investigations"),
  );
  vi.stubGlobal(
    "fetch",
    vi.fn((_url, init) => {
      signal = init.signal;
      return new Promise<Response>((done) => {
        resolve = done;
      });
    }),
  );
  fireEvent.click(screen.getByRole("button", { name: "Update view" }));
  old.unmount();
  expect(signal?.aborted).toBe(true);
  const { fetcher } = server([]);
  mount();
  await screen.findByLabelText("View name");
  await act(async () => resolve(Response.json({ data: initial() })));
  expect(screen.getByLabelText("View name")).toHaveValue("");
  expect(fetcher).toHaveBeenCalledTimes(1);
});
it("restores remote log filters and column choices without repeated resets during query changes", async () => {
  const view = initial();
  view.workspace = "logs";
  view.settings = {
    ...settings,
    search: "POST",
    filter: "failure",
    route: "one",
    minimumStatus: 400,
    historical: true,
    columns: ["method", "statusCode"],
  };
  const { fetcher } = server([view]);
  mount(<LogsPage />);
  await waitFor(() =>
    expect(screen.getByLabelText("Source")).toHaveValue("history"),
  );
  expect(screen.getByLabelText("Search", { exact: true })).toHaveValue(
    "failure",
  );
  expect(screen.getByLabelText("Route", { exact: true })).toHaveValue("one");
  expect(screen.getByLabelText("Minimum status")).toHaveValue(400);
  await waitFor(() =>
    expect(screen.getByLabelText("Filter records")).toHaveValue("POST"),
  );
  expect(screen.getByRole("columnheader", { name: "Method" })).toBeVisible();
  expect(
    screen.queryByRole("columnheader", { name: "Path" }),
  ).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("Search", { exact: true }), {
    target: { value: "edited" },
  });
  await waitFor(() =>
    expect(
      fetcher.mock.calls.some(([url]) =>
        url.includes("keyword=edited&routeId=one&statusCodeMin=400"),
      ),
    ).toBe(true),
  );
  expect(screen.getByLabelText("Search", { exact: true })).toHaveValue(
    "edited",
  );
});
it("hides unsupported persistence and recovers failed reads without hiding table data", async () => {
  vi.stubGlobal(
    "fetch",
    vi
      .fn()
      .mockResolvedValue(
        Response.json({ data: { available: false, views: [] } }),
      ),
  );
  const first = mount();
  await waitFor(() =>
    expect(screen.queryByRole("status")).not.toBeInTheDocument(),
  );
  expect(screen.queryByLabelText("View name")).not.toBeInTheDocument();
  expect(screen.getByText("needle-one")).toBeVisible();
  first.unmount();
  const fetcher = vi
    .fn()
    .mockResolvedValueOnce(
      Response.json({ message: "Read failed" }, { status: 503 }),
    )
    .mockResolvedValue(Response.json({ data: { available: true, views: [] } }));
  vi.stubGlobal("fetch", fetcher);
  mount();
  await screen.findByRole("alert");
  fireEvent.click(screen.getByRole("button", { name: "Reload saved views" }));
  await screen.findByLabelText("View name");
});
it("requires a visible column and tolerates a removed saved column", async () => {
  server([{ ...initial(), settings: { ...settings, columns: ["removed"] } }]);
  mount();
  await waitFor(() =>
    expect(screen.getByLabelText("View name")).toHaveValue("Investigations"),
  );
  expect(screen.getByRole("columnheader", { name: "Route" })).toBeVisible();
  fireEvent.click(screen.getByText("Visible columns"));
  fireEvent.click(
    screen.getByRole("checkbox", { name: "Show column: Status" }),
  );
  expect(
    screen.getByRole("checkbox", { name: "Show column: Route" }),
  ).toBeDisabled();
});

it("clears a deleted selection after conflict reload while preserving its unsaved draft", async () => {
  const { state } = server([initial()]);
  mount();
  await waitFor(() =>
    expect(screen.getByLabelText("View name")).toHaveValue("Investigations"),
  );
  fireEvent.change(screen.getByLabelText("Filter records"), {
    target: { value: "keep this draft" },
  });
  state.views = [];
  state.fail = true;
  fireEvent.click(screen.getByRole("button", { name: "Update view" }));
  await screen.findByRole("alert");
  fireEvent.click(screen.getByRole("button", { name: "Reload saved views" }));
  await waitFor(() =>
    expect(screen.getByRole("button", { name: "Update view" })).toBeDisabled(),
  );
  expect(screen.getByLabelText("Filter records")).toHaveValue(
    "keep this draft",
  );
  expect(screen.getByLabelText("View name")).toHaveValue("Investigations");
  expect(screen.getByRole("button", { name: "Save new view" })).toBeEnabled();
});
