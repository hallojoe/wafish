import { useState } from "react";
import { Plus, Download, RefreshCw } from "lucide-react";
import {
  api,
  field,
  object,
  rows,
  text,
  idPath,
  setField,
  saveBlob,
} from "@/api/client";
import type { JsonObject } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Checkbox } from "@/components/ui/checkbox";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  PageTitle,
  Panel,
  Records,
  QueryState,
  Field,
  JsonEditor,
  JsonView,
  ErrorNotice,
  StateBadge,
  Confirm,
  parseObject,
} from "@/components/workspace";
const format = (value: unknown) => JSON.stringify(value, null, 2);
export function ConfigurationPage({ kind }: { kind: "routes" | "clusters" }) {
  const isRoute = kind === "routes";
  const idKey = isRoute ? "routeId" : "clusterId";
  const { t } = useLocale();
  const query = useApi(`api/${kind}`);
  const clusters = useApi("api/clusters");
  const mutation = useWrite();
  const [selection, setSelection] = useState<string[]>([]);
  const [editor, setEditor] = useState<{
    original: string;
    id: string;
    draft: string;
  } | null>(null);
  const [tab, setTab] = useState("common");
  const [error, setError] = useState<unknown>();
  const [rename, setRename] = useState<{
    row: JsonObject;
    name: string;
  } | null>(null);
  const data = rows(query.data);
  const editable = data.filter((row) => field(row, "isEditable") !== false);
  async function open(row?: JsonObject) {
    setError(undefined);
    try {
      const id = row ? text(field(row, idKey), "") : "";
      const native = row
        ? field(field(await api("api/config/export"), "ReverseProxy"), kind)
        : undefined;
      const draft = row
        ? field(native, id)
        : isRoute
          ? {
              ClusterId: text(field(rows(clusters.data)[0], "clusterId"), ""),
              Match: { Path: "/api/{**catch-all}" },
              Order: 0,
            }
          : {
              LoadBalancingPolicy: "RoundRobin",
              Destinations: { d1: { Address: "http://localhost:8080/" } },
            };
      if (row && !draft) throw new Error("The gateway API is unavailable.");
      setEditor({ original: id, id, draft: format(draft) });
      setTab("common");
    } catch (e) {
      setError(e);
    }
  }
  const parsed = (() => {
    try {
      return parseObject(editor?.draft ?? "{}");
    } catch {
      return null;
    }
  })();
  function change(key: string, value: unknown) {
    if (editor && parsed)
      setEditor({ ...editor, draft: format(setField(parsed, key, value)) });
  }
  async function save() {
    if (!editor) return;
    setError(undefined);
    try {
      if (!editor.id.trim()) throw new Error("Required");
      await mutation.mutateAsync({
        path: `api/config/${kind}/${idPath(editor.id.trim())}`,
        method: "PUT",
        body: parseObject(editor.draft),
      });
      setEditor(null);
    } catch (e) {
      setError(e);
    }
  }
  async function batch(action: string) {
    await mutation.mutateAsync({
      path: `api/config/${kind}/batch-${action === "delete" ? "delete" : "enabled"}`,
      method: "POST",
      body: {
        [isRoute ? "routeIds" : "clusterIds"]: selection,
        enabled: action === "enable",
      },
    });
    setSelection([]);
  }
  return (
    <>
      <PageTitle
        title={isRoute ? "Routes" : "Clusters"}
        subtitle="Manage traffic. Understand every change."
      >
        <Button variant="outline" onClick={() => void query.refetch()}>
          <RefreshCw />
          {t("Refresh")}
        </Button>
        <Button onClick={() => void open()}>
          <Plus />
          {t(isRoute ? "New route" : "New cluster")}
        </Button>
      </PageTitle>
      {!editor && <ErrorNotice error={error} />}
      <Panel
        title={isRoute ? "Routes" : "Clusters"}
        actions={
          <span className="text-sm text-muted-foreground">{data.length}</span>
        }
      >
        <div className="toolbar mb-4">
          <label className="flex items-center gap-2 text-sm">
            <Checkbox
              aria-label={t("Select all")}
              checked={
                editable.length > 0 &&
                editable.every((row) =>
                  selection.includes(text(field(row, idKey))),
                )
              }
              onCheckedChange={(checked) =>
                setSelection(
                  checked ? editable.map((row) => text(field(row, idKey))) : [],
                )
              }
            />
            {t("Select all")}
          </label>
          {selection.length > 0 && (
            <>
              <span className="text-sm">
                {selection.length} {t("Selected")}
              </span>
              {isRoute && (
                <>
                  <Confirm
                    title="Enable selected"
                    onConfirm={() => batch("enable")}
                  >
                    {t("Enable selected")}
                  </Confirm>
                  <Confirm
                    title="Disable selected"
                    onConfirm={() => batch("disable")}
                  >
                    {t("Disable selected")}
                  </Confirm>
                </>
              )}
              <Confirm
                title="Delete selected"
                onConfirm={() => batch("delete")}
              >
                {t("Delete selected")}
              </Confirm>
            </>
          )}
        </div>
        <QueryState query={query}>
          <Records
            key={kind}
            viewKey={kind}
            data={data}
            columns={[
              {
                key: "select",
                label: "Selected",
                render: (row) => (
                  <Checkbox
                    disabled={field(row, "isEditable") === false}
                    aria-label={`${t("Selected")} ${text(field(row, idKey))}`}
                    checked={selection.includes(text(field(row, idKey)))}
                    onCheckedChange={(checked) =>
                      setSelection((old) =>
                        checked
                          ? [...old, text(field(row, idKey))]
                          : old.filter((id) => id !== field(row, idKey)),
                      )
                    }
                  />
                ),
              },
              {
                key: idKey,
                label: "Name",
                render: (row) => (
                  <span className="font-medium">{text(field(row, idKey))}</span>
                ),
              },
              ...(isRoute
                ? [
                    {
                      key: "match",
                      label: "Path",
                      render: (row: JsonObject) => (
                        <code>{text(field(field(row, "match"), "path"))}</code>
                      ),
                    },
                    { key: "clusterId", label: "Cluster" },
                    {
                      key: "enabled",
                      label: "State",
                      render: (row: JsonObject) => (
                        <StateBadge value={field(row, "enabled")} />
                      ),
                    },
                  ]
                : [
                    { key: "loadBalancingPolicy", label: "Load balancing" },
                    { key: "totalCount", label: "Destinations" },
                  ]),
              { key: "source", label: "Source" },
            ]}
            actions={(row) =>
              field(row, "isEditable") === false ? (
                <span className="text-muted-foreground">{t("Read-only")}</span>
              ) : (
                <>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => void open(row)}
                  >
                    {t("Edit")}
                  </Button>
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => {
                      setRename({ row, name: text(field(row, idKey)) });
                      setError(undefined);
                    }}
                  >
                    {t("Rename")}
                  </Button>
                  {isRoute && (
                    <Confirm
                      title={field(row, "enabled") ? "Disable" : "Enable"}
                      onConfirm={() =>
                        mutation.mutateAsync({
                          path: `api/config/routes/${idPath(text(field(row, idKey)))}/enabled`,
                          method: "PUT",
                          body: { enabled: !field(row, "enabled") },
                        })
                      }
                    >
                      {t(field(row, "enabled") ? "Disable" : "Enable")}
                    </Confirm>
                  )}
                  <Confirm
                    title="Confirm deletion"
                    description={text(field(row, idKey))}
                    onConfirm={() =>
                      mutation.mutateAsync({
                        path: `api/config/${kind}/${idPath(text(field(row, idKey)))}`,
                        method: "DELETE",
                      })
                    }
                  >
                    {t("Delete")}
                  </Confirm>
                </>
              )
            }
          />
        </QueryState>
      </Panel>
      <Dialog
        open={editor !== null}
        onOpenChange={(v) => {
          if (!v && !mutation.isPending) setEditor(null);
        }}
      >
        <DialogContent className="sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>
              {t(
                editor?.original
                  ? "Edit"
                  : isRoute
                    ? "New route"
                    : "New cluster",
              )}
            </DialogTitle>
            <DialogDescription>
              {t("This action changes gateway configuration.")}
            </DialogDescription>
          </DialogHeader>
          {editor && (
            <div className="space-y-5">
              <Field label="Name">
                <Input
                  value={editor.id}
                  disabled={!!editor.original}
                  onChange={(e) => setEditor({ ...editor, id: e.target.value })}
                  required
                />
              </Field>
              <Tabs value={tab} onValueChange={setTab}>
                <TabsList>
                  <TabsTrigger value="common">{t("Common fields")}</TabsTrigger>
                  <TabsTrigger value="json">{t("Advanced JSON")}</TabsTrigger>
                </TabsList>
                <TabsContent value="common" className="space-y-4 pt-3">
                  {!parsed ? (
                    <p role="alert">{t("Invalid JSON")}</p>
                  ) : isRoute ? (
                    <>
                      <Field label="Match path">
                        <Input
                          value={text(
                            field(field(parsed, "Match"), "Path"),
                            "",
                          )}
                          onChange={(e) =>
                            change(
                              "Match",
                              setField(
                                object(field(parsed, "Match")),
                                "Path",
                                e.target.value,
                              ),
                            )
                          }
                        />
                      </Field>
                      <Field label="Cluster">
                        <p className="text-sm text-muted-foreground">
                          Without a cluster, an active redirect can handle
                          requests. Other requests return 503.
                        </p>
                        <select
                          className="native-select"
                          aria-label="Cluster"
                          value={text(field(parsed, "ClusterId"), "")}
                          onChange={(e) => change("ClusterId", e.target.value)}
                        >
                          <option value="">—</option>
                          {rows(clusters.data).map((c) => (
                            <option key={text(field(c, "clusterId"))}>
                              {text(field(c, "clusterId"))}
                            </option>
                          ))}
                        </select>
                      </Field>
                      <Field label="Order">
                        <Input
                          type="number"
                          value={Number(field(parsed, "Order") ?? 0)}
                          onChange={(e) =>
                            change("Order", Number(e.target.value))
                          }
                        />
                      </Field>
                    </>
                  ) : (
                    <>
                      <Field label="Load balancing">
                        <select
                          className="native-select"
                          value={text(
                            field(parsed, "LoadBalancingPolicy"),
                            "RoundRobin",
                          )}
                          onChange={(e) =>
                            change("LoadBalancingPolicy", e.target.value)
                          }
                        >
                          {[
                            "RoundRobin",
                            "PowerOfTwoChoices",
                            "Random",
                            "LeastRequests",
                            "FirstAlphabetical",
                          ].map((value) => (
                            <option key={value}>{value}</option>
                          ))}
                        </select>
                      </Field>
                      {Object.entries(
                        object(field(parsed, "Destinations")),
                      ).map(([key, dest]) => (
                        <div key={key} className="flex items-end gap-2">
                          <div className="flex-1 min-w-0">
                            <Field
                              label={`${t("Destination address")} · ${key}`}
                            >
                              <Input
                                value={text(field(dest, "Address"), "")}
                                onChange={(e) =>
                                  change("Destinations", {
                                    ...object(field(parsed, "Destinations")),
                                    [key]: setField(
                                      object(dest),
                                      "Address",
                                      e.target.value,
                                    ),
                                  })
                                }
                              />
                            </Field>
                          </div>
                          <Button
                            variant="outline"
                            aria-label={`${t("Delete")} ${key}`}
                            onClick={() => {
                              const next = {
                                ...object(field(parsed, "Destinations")),
                              };
                              delete next[key];
                              change("Destinations", next);
                            }}
                          >
                            {t("Delete")}
                          </Button>
                        </div>
                      ))}
                      <Button
                        variant="outline"
                        onClick={() => {
                          const next = object(field(parsed, "Destinations"));
                          let index = 1;
                          while (`d${index}` in next) index++;
                          change("Destinations", {
                            ...next,
                            [`d${index}`]: { Address: "" },
                          });
                        }}
                      >
                        <Plus />
                        {t("Add a destination")}
                      </Button>
                    </>
                  )}
                </TabsContent>
                <TabsContent value="json">
                  <JsonEditor
                    value={editor.draft}
                    onChange={(draft) => setEditor({ ...editor, draft })}
                  />
                </TabsContent>
              </Tabs>
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
      <Dialog
        open={!!rename}
        onOpenChange={(v) => {
          if (!v) setRename(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("Rename")}</DialogTitle>
            <DialogDescription>
              {text(field(rename?.row, idKey))}
            </DialogDescription>
          </DialogHeader>
          <Field label="New name">
            <Input
              value={rename?.name ?? ""}
              onChange={(e) =>
                rename && setRename({ ...rename, name: e.target.value })
              }
            />
          </Field>
          <ErrorNotice error={error} />
          <DialogFooter>
            <Button
              disabled={mutation.isPending || !rename?.name.trim()}
              onClick={async () => {
                if (!rename) return;
                try {
                  const body: JsonObject = isRoute
                    ? { newRouteId: rename.name }
                    : { newClusterId: rename.name };
                  await mutation.mutateAsync({
                    path: `api/config/${kind}/${idPath(text(field(rename.row, idKey)))}/rename`,
                    method: "PUT",
                    body,
                  });
                  setRename(null);
                } catch (e) {
                  setError(e);
                }
              }}
            >
              {t("Rename")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
export function ImportPage() {
  const { t } = useLocale();
  const mutation = useWrite();
  const [draft, setDraft] = useState(
    '{\n  "ReverseProxy": { "Routes": {}, "Clusters": {} }\n}',
  );
  const [error, setError] = useState<unknown>();
  const [result, setResult] = useState<unknown>();
  async function validate() {
    setError(undefined);
    try {
      setResult(
        await api("api/config/validate", {
          method: "POST",
          body: JSON.stringify(parseObject(draft)),
        }),
      );
    } catch (e) {
      setError(e);
    }
  }
  return (
    <>
      <PageTitle title="Import & export">
        <Button
          variant="outline"
          onClick={async () => {
            try {
              const exported = await api("api/config/export");
              saveBlob(
                new Blob([format(exported)], { type: "application/json" }),
                "gateway-config.json",
              );
            } catch (e) {
              setError(e);
            }
          }}
        >
          <Download />
          {t("Export configuration")}
        </Button>
      </PageTitle>
      <Panel title="Configuration import">
        <div className="space-y-5">
          <p className="text-muted-foreground">
            {t("Review the configuration before importing.")}
          </p>
          <Field label="Choose file">
            <Input
              type="file"
              accept=".json,application/json"
              onChange={async (e) => {
                const file = e.target.files?.[0];
                if (file) {
                  setDraft(await file.text());
                  setResult(undefined);
                }
              }}
            />
          </Field>
          <JsonEditor
            value={draft}
            onChange={(value) => {
              setDraft(value);
              setResult(undefined);
            }}
          />
          <ErrorNotice error={error} />
          <div className="toolbar">
            <Button variant="outline" onClick={() => void validate()}>
              {t("Validate")}
            </Button>
            <Confirm
              title="Import configuration"
              onConfirm={() =>
                mutation.mutateAsync({
                  path: "api/config/import",
                  method: "POST",
                  body: parseObject(draft),
                })
              }
            >
              {t("Import configuration")}
            </Confirm>
          </div>
          {result !== undefined && (
            <div role="status">
              {field(result, "valid") === true && (
                <p>{t("Configuration is valid.")}</p>
              )}
              <JsonView value={result} />
            </div>
          )}
        </div>
      </Panel>
    </>
  );
}
