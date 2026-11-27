import { afterEach, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { SchemaFields } from "@/components/schema-fields";
import { LocaleProvider } from "@/locales/i18n";
afterEach(cleanup);
it("edits bounded object arrays and preserves defaults", () => {
  const schema = {
    properties: {
      mappings: {
        type: "array",
        maxItems: 1,
        items: {
          type: "object",
          properties: {
            claim: { type: "string" },
            required: { type: "boolean", default: true },
          },
        },
      },
    },
  };
  function Editor() {
    const [value, setValue] = useState("{}");
    return (
      <>
        <SchemaFields schema={schema} value={value} onChange={setValue} />
        <output>{value}</output>
      </>
    );
  }
  render(
    <LocaleProvider>
      <Editor />
    </LocaleProvider>,
  );
  fireEvent.click(screen.getByText("Add mappings"));
  expect(screen.getByRole("checkbox")).toBeChecked();
  fireEvent.change(screen.getByLabelText("claim"), {
    target: { value: "sub" },
  });
  expect(screen.getByRole("status").textContent).toContain('"sub"');
  expect(screen.getByText("Add mappings")).toBeDisabled();
  fireEvent.click(screen.getByText("Remove mappings 1"));
  expect(screen.queryByRole("checkbox")).toBeNull();
});
