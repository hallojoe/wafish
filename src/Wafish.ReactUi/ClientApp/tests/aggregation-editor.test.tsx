import { afterEach, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { SchemaFields } from "@/components/schema-fields";
import { LocaleProvider } from "@/locales/i18n";
afterEach(cleanup);
it("selects stable child routes and reports unavailable dependencies", () => {
  const schema = {
    properties: {
      children: {
        type: "array",
        items: {
          type: "object",
          properties: {
            routeUid: {
              type: "string",
              format: "wafish-route-uid",
              title: "Child route",
            },
            query: {
              type: "array",
              items: {
                type: "object",
                properties: {
                  name: { type: "string" },
                  value: { type: "string" },
                },
              },
            },
          },
        },
      },
    },
  };
  function Editor() {
    const [value, setValue] = useState('{"children":[{"routeUid":"missing"}]}');
    return (
      <>
        <SchemaFields
          schema={schema}
          value={value}
          onChange={setValue}
          routeChoices={[{ value: "stable-uid", label: "Customer profiles" }]}
        />
        <output>{value}</output>
      </>
    );
  }
  render(
    <LocaleProvider>
      <Editor />
    </LocaleProvider>,
  );
  expect(screen.getByRole("alert")).toHaveTextContent("unavailable");
  fireEvent.change(screen.getByLabelText("Child route"), {
    target: { value: "stable-uid" },
  });
  expect(screen.queryByRole("alert")).toBeNull();
  expect(screen.getByRole("status")).toHaveTextContent("stable-uid");
  fireEvent.click(screen.getByText("Add query"));
  fireEvent.change(screen.getByLabelText("name"), {
    target: { value: "customer" },
  });
  fireEvent.change(screen.getByLabelText("value"), {
    target: { value: "{route.id}" },
  });
  expect(screen.getByRole("status")).toHaveTextContent("{route.id}");
});
