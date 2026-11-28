import { useState } from "react";
import { Plus, Puzzle } from "lucide-react";
import { field, object, rows, text, idPath } from "@/api/client";
import type { JsonObject } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import { Badge } from "@/components/ui/badge";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import {
  PageTitle,
  Panel,
  Records,
  QueryState,
  StateBadge,
  Confirm,
  Inspect,
  Field,
  ErrorNotice,
  JsonEditor,
  parseObject,
} from "@/components/workspace";
import { SchemaFields, schemaDefaults } from "@/components/schema-fields";
import { ExternalLoginEditor } from "@/components/external-login-editor";
import { RedirectPreview } from "@/components/redirect-preview";
import { ObjectEditor } from "@/components/json-form";
export function PluginsPage() {
  const query = useApi("api/plugins");
  const mutation = useWrite();
  const { t } = useLocale();
  const [install, setInstall] = useState<string | null>(null);
  return (
    <>
      <PageTitle title="Plugins">
        <Button onClick={() => setInstall("")}>
          <Plus />
          {t("Install plugin")}
        </Button>
        <Confirm
          title="Reset"
          onConfirm={() =>
            mutation.mutateAsync({ path: "api/plugins/reset", method: "POST" })
          }
        >
          {t("Reset")}
        </Confirm>
      </PageTitle>
      <QueryState query={query}>
        <div className="plugin-grid">
          {rows(query.data).map((row) => {
            const id = text(field(row, "pluginId"));
            return (
              <Panel
                key={id}
                title={text(field(row, "displayName"), id)}
                actions={<Puzzle className="size-4 text-muted-foreground" />}
              >
                <div className="space-y-4">
                  <div className="flex items-center gap-2">
                    <StateBadge value={field(row, "enabled")} />
                    <Badge variant="secondary">
                      {text(field(row, "version"))}
                    </Badge>
                  </div>
                  <p className="text-sm text-muted-foreground min-h-10">
                    {text(field(row, "description"), "")}
                  </p>
                  <div className="toolbar">
                    <Inspect value={row} />
                    <Confirm
                      title={field(row, "enabled") ? "Disable" : "Enable"}
                      description={id}
                      onConfirm={() =>
                        mutation.mutateAsync({
                          path: `api/plugins/${idPath(id)}/toggle`,
                          method: "POST",
                          body: { enabled: !field(row, "enabled") },
                        })
                      }
                    >
                      {t(field(row, "enabled") ? "Disable" : "Enable")}
                    </Confirm>
                    {field(row, "isBuiltIn") === false && (
                      <>
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => setInstall(id)}
                        >
                          {t("Upgrade")}
                        </Button>
                        <Confirm
                          title="Confirm deletion"
                          description={id}
                          onConfirm={() =>
                            mutation.mutateAsync({
                              path: `api/plugins/${idPath(id)}`,
                              method: "DELETE",
                            })
                          }
                        >
                          {t("Delete")}
                        </Confirm>
                      </>
                    )}
                  </div>
                </div>
              </Panel>
            );
          })}
        </div>
      </QueryState>
      {install !== null && (
        <ObjectEditor
          title={install ? "Upgrade" : "Install plugin"}
          initial={{ sourceDirectory: "" }}
          fields={[{ key: "sourceDirectory", label: "Source directory" }]}
          path={
            install
              ? `api/plugins/${idPath(install)}/upgrade`
              : "api/plugins/install"
          }
          onClose={() => setInstall(null)}
        />
      )}
    </>
  );
}
export function BindingsPage() {
  const query = useApi("api/plugin-bindings");
  const plugins = useApi("api/plugin-bindings/plugins");
  const routes = useApi("api/routes");
  const clusters = useApi("api/clusters");
  const mutation = useWrite();
  const { t } = useLocale();
  const [editor, setEditor] = useState<JsonObject | null>(null);
  const [error, setError] = useState<unknown>();
  const [selected, setSelected] = useState<string[]>([]);
  const [batch, setBatch] = useState(false);
  const update = (key: string, value: unknown) =>
    setEditor((old) => (old ? { ...old, [key]: value } : null));
  function open(row?: JsonObject) {
    setError(undefined);
    setBatch(false);
    setEditor(
      row
        ? {
            id: field(row, "id"),
            pluginId: field(row, "pluginId"),
            scope:
              typeof field(row, "scope") === "number"
                ? Number(field(row, "scope")) === 1
                  ? "Route"
                  : "Cluster"
                : field(row, "scope"),
            scopeId: field(row, "scopeId"),
            configJson: field(row, "configJson") ?? "{}",
            schemaVersion: field(row, "schemaVersion") ?? 1,
            order: field(row, "order") ?? 0,
            enabled: field(row, "enabled"),
          }
        : {
            pluginId: text(field(rows(plugins.data)[0], "pluginId"), ""),
            scope: "Route",
            scopeId: "",
            configJson: "{}",
            schemaVersion: 1,
            order: 0,
            enabled: true,
          },
    );
  }
  const plugin = rows(plugins.data).find(
    (p) => field(p, "pluginId") === field(editor, "pluginId"),
  );
  const schema = (() => {
    try {
      return JSON.parse(
        text(
          field(
            rows(field(plugin, "schemas")).find(
              (s) =>
                Number(field(s, "version")) ===
                Number(field(editor, "schemaVersion")),
            ),
            "configJsonSchema",
          ),
          "{}",
        ),
      );
    } catch {
      return {};
    }
  })();
  const targets =
    field(editor, "scope") === "Cluster"
      ? rows(clusters.data)
      : rows(routes.data);
  async function save() {
    if (!editor) return;
    try {
      parseObject(text(editor.configJson));
      const id = text(editor.id, "");
      await mutation.mutateAsync({
        path: batch
          ? "api/plugin-bindings/batch-create"
          : `api/plugin-bindings${id ? "/" + idPath(id) : ""}`,
        method: id && !batch ? "PUT" : "POST",
        body: batch
          ? {
              ...editor,
              configJson: JSON.stringify({
                ...schemaDefaults(schema),
                ...parseObject(text(editor.configJson)),
              }),
              scopeIds: text(editor.scopeId, "")
                .split(",")
                .map((s) => s.trim())
                .filter(Boolean),
            }
          : {
              ...editor,
              configJson: JSON.stringify({
                ...schemaDefaults(schema),
                ...parseObject(text(editor.configJson)),
              }),
            },
      });
      setEditor(null);
    } catch (e) {
      setError(e);
    }
  }
  return (
    <>
      <PageTitle title="Bindings">
        <Button onClick={() => open()}>
          <Plus />
          {t("New binding")}
        </Button>
      </PageTitle>
      <Panel title="Bindings">
        <div className="toolbar mb-4">
          {selected.length > 0 && (
            <>
              <span>
                {selected.length} {t("Selected")}
              </span>
              {["Enable selected", "Disable selected", "Delete selected"].map(
                (label, i) => (
                  <Confirm
                    key={label}
                    title={label}
                    onConfirm={async () => {
                      await mutation.mutateAsync({
                        path: `api/plugin-bindings/${i === 2 ? "batch-delete" : "batch-enabled"}`,
                        method: "POST",
                        body: { bindingIds: selected, enabled: i === 0 },
                      });
                      setSelected([]);
                    }}
                  >
                    {t(label)}
                  </Confirm>
                ),
              )}
            </>
          )}
        </div>
        <QueryState query={query}>
          <Records
            data={rows(query.data)}
            columns={[
              {
                key: "selected",
                label: "Selected",
                render: (row) => (
                  <Checkbox
                    aria-label={`${t("Selected")} ${text(field(row, "pluginId"))} ${text(field(row, "scopeId"))}`}
                    checked={selected.includes(text(field(row, "id")))}
                    onCheckedChange={(v) =>
                      setSelected((old) =>
                        v
                          ? [...old, text(field(row, "id"))]
                          : old.filter((id) => id !== field(row, "id")),
                      )
                    }
                  />
                ),
              },
              { key: "pluginId", label: "Plugin" },
              { key: "scope", label: "Scope" },
              { key: "scopeId", label: "Target" },
              {
                key: "enabled",
                label: "State",
                render: (row) => <StateBadge value={field(row, "enabled")} />,
              },
            ]}
            actions={(row) => (
              <>
                <Button size="sm" variant="outline" onClick={() => open(row)}>
                  {t("Edit")}
                </Button>
                <Confirm
                  title="Confirm deletion"
                  onConfirm={() =>
                    mutation.mutateAsync({
                      path: `api/plugin-bindings/${idPath(text(field(row, "id")))}`,
                      method: "DELETE",
                    })
                  }
                >
                  {t("Delete")}
                </Confirm>
              </>
            )}
          />
        </QueryState>
      </Panel>
      <Dialog
        open={!!editor}
        onOpenChange={(v) => {
          if (!v && !mutation.isPending) setEditor(null);
        }}
      >
        <DialogContent className="sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>{t(editor?.id ? "Edit" : "New binding")}</DialogTitle>
            <DialogDescription>
              {t("This action changes gateway configuration.")}
            </DialogDescription>
          </DialogHeader>
          {editor && (
            <div className="space-y-4">
              <div className="grid gap-4 sm:grid-cols-2">
                <Field label="Plugin">
                  <select
                    className="native-select"
                    value={text(editor.pluginId, "")}
                    onChange={(e) => {
                      const next = rows(plugins.data).find(
                        (p) => field(p, "pluginId") === e.target.value,
                      );
                      const first = rows(field(next, "schemas"))[0];
                      let defaults = {};
                      try {
                        defaults = schemaDefaults(
                          JSON.parse(
                            text(field(first, "configJsonSchema"), "{}"),
                          ),
                        );
                      } catch {
                        /* Advanced editor remains available. */
                      }
                      setEditor({
                        ...editor,
                        pluginId: e.target.value,
                        schemaVersion: field(first, "version") ?? 1,
                        configJson: JSON.stringify(defaults, null, 2),
                      });
                    }}
                  >
                    {rows(plugins.data).map((p) => (
                      <option
                        key={text(field(p, "pluginId"))}
                        value={text(field(p, "pluginId"))}
                      >
                        {text(field(p, "displayName"))}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Scope">
                  <select
                    className="native-select"
                    value={text(editor.scope)}
                    onChange={(e) => update("scope", e.target.value)}
                  >
                    {["Route", "Cluster"].map((s) => (
                      <option key={s} value={s}>
                        {t(s)}
                      </option>
                    ))}
                  </select>
                </Field>
                <Field label="Target">
                  {batch ? (
                    <Input
                      value={text(editor.scopeId, "")}
                      onChange={(e) => update("scopeId", e.target.value)}
                      placeholder={t("Comma-separated targets")}
                    />
                  ) : (
                    <select
                      className="native-select"
                      value={text(editor.scopeId, "")}
                      onChange={(e) => update("scopeId", e.target.value)}
                    >
                      <option value="">—</option>
                      {targets.map((target) => {
                        const id = text(
                          field(target, "routeId") ??
                            field(target, "clusterId"),
                        );
                        return (
                          <option key={id} value={id}>
                            {id}
                          </option>
                        );
                      })}
                    </select>
                  )}
                </Field>
                <Field label="Order">
                  <Input
                    type="number"
                    value={Number(editor.order)}
                    onChange={(e) => update("order", Number(e.target.value))}
                  />
                </Field>
                <Field label="Schema version">
                  <Input
                    type="number"
                    min={1}
                    value={Number(editor.schemaVersion)}
                    onChange={(e) =>
                      update("schemaVersion", Number(e.target.value))
                    }
                  />
                </Field>
                <Field label="Enabled">
                  <input
                    type="checkbox"
                    checked={editor.enabled === true}
                    onChange={(e) => update("enabled", e.target.checked)}
                  />
                </Field>
                {!editor.id && (
                  <Field label="Multiple targets">
                    <input
                      type="checkbox"
                      checked={batch}
                      onChange={(e) => setBatch(e.target.checked)}
                    />
                  </Field>
                )}
              </div>
              <details>
                <summary>{t("Common fields")}</summary>
                {editor.pluginId === "external-login" ? <ExternalLoginEditor
                  schema={schema}
                  value={text(editor.configJson)}
                  onChange={(value) => update("configJson", value)}
                /> : <SchemaFields
                  schema={schema}
                  routeChoices={rows(routes.data)
                    .filter(
                      (route) =>
                        text(field(route, "clusterId"), "") &&
                        text(field(route, "routeId")) !==
                          text(editor.scopeId) &&
                        text(field(route, "routeUid"), ""),
                    )
                    .map((route) => ({
                      value: text(field(route, "routeUid")),
                      label: text(field(route, "routeId")),
                    }))}
                  value={text(editor.configJson)}
                  onChange={(value) => update("configJson", value)}
                />}
              </details>
              <JsonEditor
                label="Configuration JSON"
                value={text(editor.configJson)}
                onChange={(v) => update("configJson", v)}
              />
              {editor.pluginId === "request-aggregation" && (
                <p role="status">
                  Child routes must support GET. Referenced routes cannot be
                  deleted or changed incompatibly until their references are
                  updated.
                </p>
              )}
              {editor.pluginId === "redirect" && !batch && (
                <RedirectPreview
                  binding={{ ...editor, configJson: editor.configJson }}
                />
              )}
              <Inspect label="Schema" value={field(plugin, "schemas")} />
              <ErrorNotice error={error} />
            </div>
          )}
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => setEditor(null)}
              disabled={mutation.isPending}
            >
              {t("Cancel")}
            </Button>
            <Button onClick={() => void save()} disabled={mutation.isPending}>
              {t("Save changes")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
export function PresetsPage() {
  const query = useApi("api/presets");
  const bindings = useApi("api/plugin-bindings");
  const mutation = useWrite();
  const { t } = useLocale();
  const [editor, setEditor] = useState<JsonObject | null>(null);
  const [apply, setApply] = useState<string | null>(null);
  return (
    <>
      <PageTitle title="Presets">
        <Button
          onClick={() =>
            setEditor({
              name: "",
              description: "",
              pluginId: "",
              configJson: "{}",
              schemaVersion: 1,
            })
          }
        >
          <Plus />
          {t("Save preset")}
        </Button>
      </PageTitle>
      <Panel title="Presets">
        <QueryState query={query}>
          <Records
            data={rows(query.data)}
            columns={[
              { key: "name", label: "Name" },
              { key: "pluginId", label: "Plugin" },
              { key: "description", label: "Description" },
            ]}
            actions={(row) => (
              <>
                <Inspect value={row} />
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setEditor(object(row))}
                >
                  {t("Edit")}
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setApply(text(field(row, "id")))}
                >
                  {t("Apply preset")}
                </Button>
                <Confirm
                  title="Confirm deletion"
                  onConfirm={() =>
                    mutation.mutateAsync({
                      path: `api/presets/${idPath(text(field(row, "id")))}`,
                      method: "DELETE",
                    })
                  }
                >
                  {t("Delete")}
                </Confirm>
              </>
            )}
          />
        </QueryState>
      </Panel>
      {editor && (
        <ObjectEditor
          title="Save preset"
          path="api/presets"
          initial={editor}
          fields={[
            { key: "name", label: "Name" },
            { key: "description", label: "Description" },
            { key: "pluginId", label: "Plugin" },
            { key: "schemaVersion", label: "Schema version", type: "number" },
          ]}
          onClose={() => setEditor(null)}
        />
      )}
      {apply && (
        <ObjectEditor
          title="Apply preset"
          initial={{ bindingId: text(field(rows(bindings.data)[0], "id"), "") }}
          path={`api/presets/${idPath(apply)}/apply`}
          fields={[
            {
              key: "bindingId",
              label: "Binding",
              options: rows(bindings.data).map((b) => text(field(b, "id"))),
            },
          ]}
          onClose={() => setApply(null)}
        />
      )}
    </>
  );
}
