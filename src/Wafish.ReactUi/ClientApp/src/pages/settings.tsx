import { Authenticators } from "@/components/authenticators";
import { useState } from "react";
import { api, config, download, field, object, rows, text } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  PageTitle,
  Panel,
  QueryState,
  JsonView,
  ErrorNotice,
  Field,
  Confirm,
  Records,
  Inspect,
  StateBadge,
} from "@/components/workspace";
import { SettingsPanel, ObjectEditor } from "@/components/json-form";
function ReadPanel({ title, path }: { title: string; path: string }) {
  const query = useApi(path);
  return (
    <Panel title={title}>
      <QueryState query={query}>
        <JsonView value={query.data} />
      </QueryState>
    </Panel>
  );
}
export function SystemPage() {
  const { t } = useLocale();
  const [error, setError] = useState<unknown>();
  const mutation = useWrite();
  return (
    <>
      <PageTitle title="System">
        <Button
          variant="outline"
          onClick={async () => {
            try {
              await download(
                "api/settings/database/backup",
                "gateway-backup.db",
              );
            } catch (e) {
              setError(e);
            }
          }}
        >
          {t("Download backup")}
        </Button>
      </PageTitle>
      <div className="space-y-6">
        <ErrorNotice error={error} />
        <div className="grid gap-6 lg:grid-cols-2">
          <ReadPanel title="System" path="api/settings/system" />
          <ReadPanel title="Database backup" path="api/settings/database" />
        </div>
        <SettingsPanel
          title="Log settings"
          path="api/logs/settings"
          method="PUT"
          fields={[
            {
              key: "persistenceEnabled",
              label: "Persistence",
              type: "checkbox",
            },
            {
              key: "minLogLevel",
              label: "Minimum level",
              options: [
                "Trace",
                "Debug",
                "Information",
                "Warning",
                "Error",
                "Critical",
              ],
            },
            {
              key: "metaRetentionDays",
              label: "Metadata retention days",
              type: "number",
            },
            {
              key: "bodyRetentionDays",
              label: "Body retention days",
              type: "number",
            },
            {
              key: "requestBodyCaptureEnabled",
              label: "Capture request bodies",
              type: "checkbox",
            },
            {
              key: "responseBodyCaptureEnabled",
              label: "Capture response bodies",
              type: "checkbox",
            },
            { key: "samplingEnabled", label: "Sampling", type: "checkbox" },
            { key: "samplingRate", label: "Sampling rate", type: "number" },
            { key: "errorsOnly", label: "Errors only", type: "checkbox" },
          ]}
        />
        <Confirm
          title="Reset"
          onConfirm={() =>
            mutation.mutateAsync({
              path: "api/logs/settings/reset",
              method: "PUT",
            })
          }
        >
          {t("Reset")}
        </Confirm>
        <ReadPanel
          title="Restart required settings"
          path="api/logs/settings/restart-required"
        />
        <ReadPanel title="Log statistics" path="api/logs/stats" />
      </div>
    </>
  );
}
export function NotificationsPage() {
  const { t } = useLocale();
  const mutation = useWrite();
  const history = useApi("api/webhook/history");
  const [test, setTest] = useState(false);
  return (
    <>
      <PageTitle title="Notifications">
        <Button onClick={() => setTest(true)}>{t("Send test")}</Button>
        <Confirm
          title="Clear cooldown"
          onConfirm={() =>
            mutation.mutateAsync({
              path: "api/webhook/cooldown/clear",
              method: "POST",
            })
          }
        >
          {t("Clear cooldown")}
        </Confirm>
      </PageTitle>
      <div className="space-y-6">
        <SettingsPanel
          title="Notification settings"
          path="api/webhook/settings"
          transform={(value) => {
            const result = object(value);
            const platforms = Object.fromEntries(
              Object.entries(result)
                .filter(
                  ([key, v]) => Array.isArray(v) && key !== "enabledEvents",
                )
                .map(([key, endpoints]) => [key, { endpoints }]),
            );
            return {
              platforms,
              enabledEvents: result.enabledEvents,
              platformEvents: result.platformEvents,
              cooldownSeconds: result.cooldownSeconds,
              retryCount: result.retryCount,
              timeoutSeconds: result.timeoutSeconds,
            };
          }}
          fields={[
            {
              key: "cooldownSeconds",
              label: "Cooldown seconds",
              type: "number",
            },
            { key: "retryCount", label: "Retry count", type: "number" },
            { key: "timeoutSeconds", label: "Timeout seconds", type: "number" },
          ]}
        />
        <Panel
          title="Delivery history"
          actions={
            <Confirm
              title="Clear history"
              onConfirm={() =>
                mutation.mutateAsync({
                  path: "api/webhook/history",
                  method: "DELETE",
                })
              }
            >
              {t("Clear history")}
            </Confirm>
          }
        >
          <QueryState query={history}>
            <Records
              data={rows(history.data)}
              columns={[
                { key: "createdAt", label: "Time" },
                { key: "platform", label: "Platform" },
                { key: "eventType", label: "Operation" },
                { key: "success", label: "Success" },
              ]}
              actions={(row) => <Inspect value={row} />}
            />
          </QueryState>
        </Panel>
      </div>
      {test && (
        <ObjectEditor
          title="Send test"
          initial={{ platform: "slack" }}
          fields={[{ key: "platform", label: "Platform" }]}
          path="api/webhook/test"
          onClose={() => setTest(false)}
        />
      )}
    </>
  );
}
export function AccountPage() {
  const query = useApi("api/auth/status");
  const jwt = config.authMode.endsWith("Jwt");
  const factor = useApi("api/2fa/status", jwt);
  const mutation = useWrite();
  const [setup, setSetup] = useState<unknown>();
  const [code, setCode] = useState("");
  const [error, setError] = useState<unknown>();
  const { t } = useLocale();
  return (
    <>
      <PageTitle title="Account" />
      <div className="space-y-6">
        <Panel title="Authentication">
          <QueryState query={query}>
            <dl className="details-grid">
              <dt>{t("Authentication")}</dt>
              <dd>{text(field(query.data, "authMode"))}</dd>
              <dt>{t("State")}</dt>
              <dd>
                <StateBadge value={field(query.data, "isAuthEnabled")} />
              </dd>
            </dl>
          </QueryState>
        </Panel>
        {jwt && config.webAuthn && (
          <Authenticators
            requiresTwoFactor={Boolean(field(factor.data, "enabled"))}
          />
        )}
        {jwt && (
          <Panel title="Two-factor authentication">
            <QueryState query={factor}>
              <div className="toolbar">
                <StateBadge value={field(factor.data, "enabled")} />
                {field(factor.data, "enabled") ? (
                  <Confirm
                    title="Disable"
                    onConfirm={() =>
                      mutation.mutateAsync({
                        path: "api/2fa/disable",
                        method: "POST",
                      })
                    }
                  >
                    {t("Disable")}
                  </Confirm>
                ) : (
                  <Button
                    onClick={async () => {
                      try {
                        setSetup(await api("api/2fa/setup"));
                        setError(undefined);
                      } catch (e) {
                        setError(e);
                      }
                    }}
                  >
                    {t("Set up")}
                  </Button>
                )}
              </div>
              {setup !== undefined && (
                <div className="space-y-4 mt-5">
                  <p className="text-sm text-muted-foreground">
                    {t("Add this secret to your authenticator.")}
                  </p>
                  <Field label="Secret">
                    <Input readOnly value={text(field(setup, "secret"))} />
                  </Field>
                  <Field label="Verification code">
                    <Input
                      autoComplete="one-time-code"
                      inputMode="numeric"
                      value={code}
                      onChange={(e) => setCode(e.target.value)}
                    />
                  </Field>
                  <Button
                    disabled={mutation.isPending || !code}
                    onClick={async () => {
                      try {
                        await mutation.mutateAsync({
                          path: "api/2fa/verify",
                          method: "POST",
                          body: { code, secret: field(setup, "secret") },
                        });
                        setSetup(undefined);
                        setCode("");
                        setError(undefined);
                      } catch (e) {
                        setError(e);
                      }
                    }}
                  >
                    {t("Verify and enable")}
                  </Button>
                </div>
              )}
              <ErrorNotice error={error} />
            </QueryState>
          </Panel>
        )}
      </div>
    </>
  );
}
export function IntegrationPage() {
  const [result, setResult] = useState<unknown>();
  const [error, setError] = useState<unknown>();
  const { t } = useLocale();
  if (!config.ai)
    return (
      <>
        <PageTitle title="AI configuration" />
        <Panel title="Optional integration">
          <p>{t("Disabled in this host.")}</p>
        </Panel>
      </>
    );
  return (
    <>
      <PageTitle title="AI configuration">
        <Button
          variant="outline"
          onClick={async () => {
            try {
              setResult(await api("api/ai/test", { method: "POST" }));
              setError(undefined);
            } catch (e) {
              setError(e);
            }
          }}
        >
          {t("Test connection")}
        </Button>
      </PageTitle>
      <div className="space-y-6">
        <SettingsPanel
          title="AI configuration"
          path="api/ai/config"
          transform={(value) => ({ ...object(value), apiKey: null })}
          fields={[
            { key: "enabled", label: "Enabled", type: "checkbox" },
            { key: "provider", label: "Provider" },
            { key: "baseUrl", label: "Endpoint" },
            { key: "chatModel", label: "Model" },
            { key: "apiKey", label: "API key", type: "password" },
            { key: "maxTokens", label: "Maximum tokens", type: "number" },
            { key: "temperature", label: "Temperature", type: "number" },
            { key: "maxHistory", label: "Maximum history", type: "number" },
          ]}
        />
        <Panel title="MCP">
          <p>
            {t(
              config.mcp
                ? "Registered by this host."
                : "Disabled in this host.",
            )}
          </p>
        </Panel>
        <ErrorNotice error={error} />
        {result !== undefined && (
          <Panel title="Result">
            <JsonView value={result} />
          </Panel>
        )}
      </div>
    </>
  );
}
