import { useState } from "react";
import { api, field, text, type JsonObject } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  ErrorNotice,
  Field,
  JsonEditor,
  parseObject,
} from "@/components/workspace";

export function RedirectPreview({ binding }: { binding: JsonObject }) {
  const [url, setUrl] = useState("https://example.com/old");
  const [method, setMethod] = useState("GET");
  const [pathBase, setPathBase] = useState("");
  const [headers, setHeaders] = useState("{}");
  const [result, setResult] = useState<unknown>();
  const [error, setError] = useState<unknown>();
  const [pending, setPending] = useState(false);
  // Store the input identity with the response. Editing inputs immediately hides stale results.
  const fingerprint = JSON.stringify({
    binding,
    url,
    method,
    pathBase,
    headers,
  });
  const [resultInput, setResultInput] = useState("");
  async function preview() {
    setPending(true);
    setError(undefined);
    try {
      parseObject(text(binding.configJson));
      const response = await api("api/plugins/redirect/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          binding,
          request: { url, method, pathBase, headers: parseObject(headers) },
        }),
      });
      setResult(response);
      setResultInput(fingerprint);
    } catch (e) {
      setError(e);
      setResult(undefined);
    } finally {
      setPending(false);
    }
  }
  const evaluation = field(result, "result");
  return (
    <section className="space-y-3" aria-label="Redirect preview">
      <p>
        Preview evaluates routing and the redirect target. It does not execute
        access policies or contact destinations.
      </p>
      <Field label="Sample URL">
        <Input value={url} onChange={(e) => setUrl(e.target.value)} />
      </Field>
      <Field label="Sample method">
        <Input value={method} onChange={(e) => setMethod(e.target.value)} />
      </Field>
      <Field label="Sample path base">
        <Input value={pathBase} onChange={(e) => setPathBase(e.target.value)} />
      </Field>
      <JsonEditor
        label="Sample matching headers"
        value={headers}
        onChange={setHeaders}
      />
      <Button
        type="button"
        variant="outline"
        disabled={pending}
        onClick={() => void preview()}
      >
        Preview redirect
      </Button>
      <ErrorNotice error={error} />
      {result !== undefined && resultInput === fingerprint && (
        <dl aria-live="polite">
          <dt>Outcome</dt>
          <dd>{text(field(evaluation, "outcome"))}</dd>
          <dt>Matched route</dt>
          <dd>{text(field(result, "matchedRouteId"))}</dd>
          <dt>Revision</dt>
          <dd>{text(field(result, "configurationRevision"))}</dd>
          <dt>Status</dt>
          <dd>{text(field(evaluation, "statusCode"))}</dd>
          <dt>Location</dt>
          <dd className="break-all">{text(field(evaluation, "location"))}</dd>
          {field(evaluation, "error") != null && (
            <>
              <dt>Error</dt>
              <dd>{text(field(evaluation, "error"))}</dd>
            </>
          )}
        </dl>
      )}
    </section>
  );
}
