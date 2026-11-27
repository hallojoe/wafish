import { useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import {
  api,
  authHeaders,
  requestCredentials,
  config,
  field,
  idPath,
  rows,
  text,
  write,
} from "@/api/client";
import type { JsonObject } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import {
  PageTitle,
  QueryState,
  Panel,
  ErrorNotice,
  Field,
  Confirm,
  JsonView,
} from "@/components/workspace";

const commonActions = [
  {
    title: "List routes",
    prompt:
      "List my routes with their matching paths, clusters, and enabled state.",
  },
  {
    title: "Inspect backends",
    prompt: "List my clusters and their configured backend destinations.",
  },
  {
    title: "Review traffic",
    prompt:
      "Summarize the available traffic statistics and highlight errors or unusual patterns.",
  },
  {
    title: "List plugins",
    prompt: "List the available plugins and explain what each one does.",
  },
  {
    title: "Troubleshoot a route",
    prompt:
      "Help me troubleshoot a route. Ask which route, then inspect its match and backend configuration.",
  },
  {
    title: "Create a route",
    prompt:
      "Help me create a route. Ask for its name, match path, cluster, and backend address before proposing changes.",
  },
];

export function AssistantPage() {
  const [session, setSession] = useState("");
  const [message, setMessage] = useState("");
  const [output, setOutput] = useState("");
  const [events, setEvents] = useState<JsonObject[]>([]);
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const messageInput = useRef<HTMLTextAreaElement>(null);
  const queryClient = useQueryClient();
  const mutation = useWrite();
  const { t } = useLocale();
  const sessions = useApi("api/ai/sessions", config.ai);
  const messages = useApi(
    `api/ai/sessions/${idPath(session)}/messages`,
    config.ai && !!session && !busy,
  );
  useEffect(() => () => controller.current?.abort(), []);
  async function send() {
    if (!message.trim()) return;
    setBusy(true);
    setError(undefined);
    setOutput("");
    setEvents([]);
    const abort = new AbortController();
    controller.current = abort;
    try {
      const response = await fetch(`${config.apiBase}/api/ai/chat`, {
        method: "POST",
        credentials: requestCredentials(),
        headers: { "Content-Type": "application/json", ...authHeaders() },
        body: JSON.stringify({ sessionId: session || null, message }),
        signal: abort.signal,
      });
      if (!response.ok || !response.body) {
        if (response.status === 401) await api("api/auth/status");
        throw new Error(`HTTP ${response.status}`);
      }
      setMessage("");
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let pending = "";
      while (true) {
        const { done, value } = await reader.read();
        pending += decoder.decode(value, { stream: !done });
        const lines = pending.split("\n");
        pending = lines.pop() ?? "";
        for (const line of lines) {
          if (!line.startsWith("data:")) continue;
          const event: JsonObject = JSON.parse(line.slice(5).trim());
          const kind = field(event, "type");
          if (kind === "session")
            setSession(text(field(event, "sessionId"), ""));
          else if (kind === "chunk")
            setOutput((old) => old + text(field(event, "content"), ""));
          else if (kind === "error")
            setError(new Error(text(field(event, "error"))));
          else if (kind === "tool_call" || kind === "tool_result")
            setEvents((old) => [...old, event]);
        }
        if (done) break;
      }
    } catch (e) {
      if (!abort.signal.aborted) setError(e);
    } finally {
      setBusy(false);
      controller.current = null;
      void queryClient.invalidateQueries({ queryKey: ["api/ai/sessions"] });
      void queryClient.invalidateQueries({
        queryKey: [`api/ai/sessions/${idPath(session)}/messages`],
      });
    }
  }
  if (!config.ai)
    return (
      <>
        <PageTitle title="Assistant" />
        <p>{t("Disabled in this host.")}</p>
      </>
    );
  return (
    <>
      <PageTitle title="Assistant">
        <Button
          variant="outline"
          disabled={busy}
          onClick={() => {
            setSession("");
            setOutput("");
            setEvents([]);
          }}
        >
          {t("New session")}
        </Button>
      </PageTitle>
      <div className="grid gap-6 lg:grid-cols-[15rem_1fr]">
        <Panel title="Session">
          <QueryState query={sessions}>
            <div className="space-y-3">
              {rows(sessions.data).map((row) => {
                const id = text(field(row, "sessionId") ?? field(row, "id"));
                return (
                  <div key={id} className="space-y-2">
                    <Button
                      className="w-full justify-start overflow-hidden"
                      variant={id === session ? "secondary" : "ghost"}
                      disabled={busy}
                      onClick={() => {
                        setSession(id);
                        setOutput("");
                        setEvents([]);
                      }}
                    >
                      {text(field(row, "title"), id)}
                    </Button>
                    <Confirm
                      title="Confirm deletion"
                      disabled={busy}
                      onConfirm={async () => {
                        await mutation.mutateAsync({
                          path: `api/ai/sessions/${idPath(id)}`,
                          method: "DELETE",
                        });
                        if (session === id) setSession("");
                      }}
                    >
                      {t("Delete")}
                    </Confirm>
                  </div>
                );
              })}
            </div>
          </QueryState>
        </Panel>
        <Panel title="Assistant">
          <div className="space-y-5">
            <div className="chat-messages">
              {session && !busy && (
                <QueryState query={messages}>
                  {rows(messages.data).map((row, index) => (
                    <div
                      key={index}
                      className={
                        field(row, "role") === "user"
                          ? "chat-user"
                          : "chat-assistant"
                      }
                    >
                      <p className="eyebrow mb-2">
                        {t(field(row, "role") === "user" ? "You" : "Assistant")}
                      </p>
                      <div className="whitespace-pre-wrap break-words">
                        {text(field(row, "content"))}
                      </div>
                    </div>
                  ))}
                </QueryState>
              )}
              {output && (
                <div
                  className="chat-assistant whitespace-pre-wrap break-words"
                  role="status"
                >
                  {output}
                </div>
              )}
            </div>
            {events.map((event, i) => (
              <div key={i} className="rounded-lg border p-4">
                <JsonView value={event} />
                {field(event, "isWriteTool") === true && (
                  <Confirm
                    title="Run tool"
                    description={text(field(event, "toolName"))}
                    onConfirm={async () => {
                      const result = await write(
                        "api/ai/tool/execute",
                        "POST",
                        {
                          toolName: field(event, "toolName"),
                          arguments: field(event, "toolArgs"),
                        },
                      );
                      setEvents((old) =>
                        old.map((e, n) =>
                          i === n ? { ...e, isWriteTool: false, result } : e,
                        ),
                      );
                      void queryClient.invalidateQueries();
                    }}
                  >
                    {t("Run tool")}
                  </Confirm>
                )}
              </div>
            ))}
            <ErrorNotice error={error} />
            <section
              aria-labelledby="assistant-common-actions"
              className="space-y-3"
            >
              <h3 id="assistant-common-actions" className="font-medium">
                {t("Common actions")}
              </h3>
              <p className="text-sm text-muted-foreground">
                {t(
                  message.trim()
                    ? "Clear your draft to choose another action."
                    : "Choose an action, review the message, then send it.",
                )}
              </p>
              <div className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
                {commonActions.map((action) => (
                  <Button
                    key={action.title}
                    type="button"
                    variant="outline"
                    className="h-auto min-w-0 flex-col items-start gap-1 whitespace-normal p-3 text-left"
                    disabled={busy || !!message.trim()}
                    onClick={() => {
                      setMessage(t(action.prompt));
                      messageInput.current?.focus();
                    }}
                  >
                    <span className="font-medium">{t(action.title)}</span>
                    <span className="text-xs font-normal text-muted-foreground">
                      {t(action.prompt)}
                    </span>
                  </Button>
                ))}
              </div>
            </section>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                void send();
              }}
              className="space-y-3"
            >
              <Field label="Message">
                <Textarea
                  ref={messageInput}
                  value={message}
                  onChange={(e) => setMessage(e.target.value)}
                  disabled={busy}
                  rows={3}
                />
              </Field>
              <div className="toolbar justify-end">
                {busy ? (
                  <Button
                    type="button"
                    variant="outline"
                    onClick={() => controller.current?.abort()}
                  >
                    {t("Stop")}
                  </Button>
                ) : (
                  <Button type="submit" disabled={!message.trim()}>
                    {t("Send")}
                  </Button>
                )}
              </div>
            </form>
          </div>
        </Panel>
      </div>
    </>
  );
}
