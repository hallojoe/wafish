import { afterEach, expect, it } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { ExternalLoginEditor } from "@/components/external-login-editor";
import { LocaleProvider } from "@/locales/i18n";
afterEach(cleanup);
const schema = { "x-wafish-api-targets": { website: ["support"], portal: ["orders"] } };
function Editor() {
  const [value, setValue] = useState('{"site":"website","mode":"api","apiTarget":"support"}');
  return <LocaleProvider><ExternalLoginEditor schema={schema} value={value} onChange={setValue} /><output>{value}</output></LocaleProvider>;
}
it("restricts token targets to the selected site and clears old selections", () => {
  render(<Editor />);
  expect(screen.getByRole("option", { name: "support" })).toBeInTheDocument();
  expect(screen.queryByRole("option", { name: "orders" })).toBeNull();
  fireEvent.change(screen.getByLabelText("Site"), { target: { value: "portal" } });
  expect(screen.getByRole("status")).not.toHaveTextContent("apiTarget");
  expect(screen.getByRole("option", { name: "orders" })).toBeInTheDocument();
  fireEvent.change(screen.getByLabelText("API token target"), { target: { value: "orders" } });
  expect(screen.getByRole("status")).toHaveTextContent("orders");
  fireEvent.change(screen.getByLabelText("API token target"), { target: { value: "" } });
  expect(screen.getByRole("status")).not.toHaveTextContent("apiTarget");
});
it("clears API credentials when changing a route to page mode", () => {
  render(<Editor />);
  fireEvent.change(screen.getByLabelText("Request type"), { target: { value: "page" } });
  expect(screen.queryByLabelText("API token target")).toBeNull();
  expect(screen.getByRole("status")).not.toHaveTextContent("apiTarget");
});
it("reports missing host configuration and invalid JSON", () => {
  const { rerender } = render(<LocaleProvider><ExternalLoginEditor schema={{}} value="{}" onChange={() => {}} /></LocaleProvider>);
  expect(screen.getByRole("alert")).toHaveTextContent("host settings");
  rerender(<LocaleProvider><ExternalLoginEditor schema={{}} value="{" onChange={() => {}} /></LocaleProvider>);
  expect(screen.getByRole("alert")).toHaveTextContent("Invalid JSON");
});
