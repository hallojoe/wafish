import { afterEach, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { LocaleProvider } from "@/locales/i18n";
import { RedirectPreview } from "@/components/redirect-preview";
import { SchemaFields } from "@/components/schema-fields";

const binding = {
  pluginId: "redirect",
  scope: "Route",
  scopeId: "old",
  enabled: true,
  configJson: '{"target":"/new"}',
};
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

it("previews without saving and clears stale output after editing inputs", async () => {
  const fetch = vi.fn().mockResolvedValue(
    new Response(
      JSON.stringify({
        code: 200,
        data: {
          matchedRouteId: "old",
          configurationRevision: 12,
          result: { outcome: "Redirect", statusCode: 307, location: "/new" },
        },
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    ),
  );
  vi.stubGlobal("fetch", fetch);
  render(
    <LocaleProvider>
      <RedirectPreview binding={binding} />
    </LocaleProvider>,
  );
  fireEvent.change(screen.getByLabelText("Sample URL"), {
    target: { value: "https://example.com/old" },
  });
  fireEvent.change(screen.getByLabelText("Sample method"), {
    target: { value: "POST" },
  });
  fireEvent.change(screen.getByLabelText("Sample path base"), {
    target: { value: "/edge" },
  });
  fireEvent.click(screen.getByRole("button", { name: "Preview redirect" }));
  expect(await screen.findByText("/new")).toBeInTheDocument();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(fetch.mock.calls[0][0]).toContain("api/plugins/redirect/preview");
  expect(JSON.parse(fetch.mock.calls[0][1].body)).toMatchObject({
    binding,
    request: { method: "POST", pathBase: "/edge" },
  });
  fireEvent.change(screen.getByLabelText("Sample URL"), {
    target: { value: "https://other.example/old" },
  });
  expect(screen.queryByText("/new")).not.toBeInTheDocument();
});

it("reports validation failures and allows another preview", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({ code: 400, message: "Host is not allowed" }),
        {
          status: 400,
          headers: { "Content-Type": "application/json" },
        },
      ),
    ),
  );
  render(
    <LocaleProvider>
      <RedirectPreview binding={binding} />
    </LocaleProvider>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Preview redirect" }));
  expect(await screen.findByRole("alert")).toHaveTextContent(
    "Host is not allowed",
  );
  await waitFor(() =>
    expect(
      screen.getByRole("button", { name: "Preview redirect" }),
    ).not.toBeDisabled(),
  );
});

it("shows evaluation errors without creating destination links", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          code: 200,
          data: {
            result: {
              outcome: "InvalidTarget",
              statusCode: 400,
              error: "A required route value is missing.",
            },
          },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  render(
    <LocaleProvider>
      <RedirectPreview binding={binding} />
    </LocaleProvider>,
  );
  fireEvent.click(screen.getByRole("button", { name: "Preview redirect" }));
  expect(
    await screen.findByText("A required route value is missing."),
  ).toBeInTheDocument();
  expect(screen.queryByRole("link")).not.toBeInTheDocument();
});

it("keeps numeric schema choices numeric and edits exact host lists", () => {
  const onChange = vi.fn();
  render(
    <LocaleProvider>
      <SchemaFields
        schema={{
          properties: {
            statusCode: { type: "integer", enum: [307, 308], default: 307 },
            allowedHosts: {
              type: "array",
              items: { type: "string" },
              default: [],
            },
          },
        }}
        value="{}"
        onChange={onChange}
      />
    </LocaleProvider>,
  );
  fireEvent.change(screen.getByLabelText("status Code"), {
    target: { value: "308" },
  });
  expect(JSON.parse(onChange.mock.calls[0][0]).statusCode).toBe(308);
  fireEvent.change(screen.getByLabelText("allowed Hosts"), {
    target: { value: "one.example\ntwo.example" },
  });
  expect(JSON.parse(onChange.mock.calls[1][0]).allowedHosts).toEqual([
    "one.example",
    "two.example",
  ]);
  fireEvent.change(screen.getByLabelText("allowed Hosts"), {
    target: { value: "one.example\n" },
  });
  expect(JSON.parse(onChange.mock.calls[2][0]).allowedHosts).toEqual([
    "one.example",
    "",
  ]);
  fireEvent.blur(screen.getByLabelText("allowed Hosts"), {
    target: { value: " one.example\n\ntwo.example " },
  });
  expect(JSON.parse(onChange.mock.calls[3][0]).allowedHosts).toEqual([
    "one.example",
    "two.example",
  ]);
});
