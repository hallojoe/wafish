import { useEffect, useRef, useState } from "react";
import { readViews, saveView, deleteView } from "@/api/operator-views";
import type { OperatorViews, ViewSettings } from "@/api/operator-views";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useLocale } from "@/locales/i18n";

export function SavedViews({
  workspace,
  settings,
  columns,
  onRestore,
}: {
  workspace: string;
  settings: ViewSettings;
  columns: { key: string; label: string }[];
  onRestore: (settings: ViewSettings) => void;
}) {
  const { t } = useLocale();
  const [data, setData] = useState<OperatorViews>();
  const [selected, setSelected] = useState("");
  const [name, setName] = useState("");
  const [error, setError] = useState<unknown>();
  const [busy, setBusy] = useState(false);
  const [reload, setReload] = useState(0);
  const restore = useRef(onRestore);
  const restored = useRef<string | undefined>(undefined);
  const currentSettings = useRef(settings);
  useEffect(() => {
    currentSettings.current = settings;
  }, [settings]);
  const lifetime = useRef<AbortController | null>(null);
  useEffect(() => {
    restore.current = onRestore;
  }, [onRestore]);
  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    const initialSettings = JSON.stringify(currentSettings.current);
    void readViews(workspace, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return;
        setData(value);
        setError(undefined);
        const active = value.views.find((view) => view.isDefault);
        if (restored.current !== workspace) {
          restored.current = workspace;
          setSelected(active?.id ?? "");
          setName(active?.name ?? "");
          if (
            active &&
            JSON.stringify(currentSettings.current) === initialSettings
          )
            restore.current(active.settings);
        } else {
          setSelected((current) =>
            value.views.some((view) => view.id === current) ? current : "",
          );
        }
      })
      .catch((reason) => {
        if (!controller.signal.aborted) setError(reason);
      });
    return () => controller.abort();
  }, [workspace, reload]);

  async function change(
    action: "create" | "update" | "select" | "delete",
    id = selected,
  ) {
    const controller = lifetime.current;
    if (!controller || controller.signal.aborted || busy) return;
    const current = data?.views.find((view) => view.id === id);
    setBusy(true);
    setError(undefined);
    try {
      if (action === "delete") {
        if (!current) return;
        await deleteView(workspace, current, controller.signal);
      } else {
        const saved = await saveView(
          workspace,
          {
            id: action === "create" ? crypto.randomUUID() : current!.id,
            name: action === "select" ? current!.name : name.trim(),
            settings: action === "select" ? current!.settings : settings,
            isDefault: true,
            revision: action === "create" ? 0 : current!.revision,
          },
          controller.signal,
        );
        if (controller.signal.aborted) return;
        setSelected(saved.id);
        setName(saved.name);
        if (action === "select") restore.current(saved.settings);
      }
      const refreshed = await readViews(workspace, controller.signal);
      if (controller.signal.aborted) return;
      setData(refreshed);
      if (action === "delete") {
        setSelected("");
        setName("");
      }
    } catch (reason) {
      if (!controller.signal.aborted) setError(reason);
    } finally {
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  if (data && !data.available) return null;
  return (
    <section aria-label={t("Saved views")} className="space-y-3 mb-4">
      {error != null && (
        <div role="alert" className="text-sm text-destructive">
          {error instanceof Error
            ? error.message
            : t("Unable to load saved views")}
          <p>
            {t(
              "Current filters are unchanged. Reload saved views before retrying a conflict.",
            )}
          </p>
          <Button
            variant="outline"
            disabled={busy}
            onClick={() => setReload((value) => value + 1)}
          >
            {t("Reload saved views")}
          </Button>
        </div>
      )}
      {!data ? (
        <p role="status">{t("Loading saved views")}</p>
      ) : (
        <>
          <div className="toolbar">
            <label className="space-y-1">
              <span>{t("Saved view")}</span>
              <select
                className="native-select"
                aria-label={t("Saved view")}
                value={selected}
                disabled={busy}
                onChange={(event) => {
                  if (event.target.value)
                    void change("select", event.target.value);
                }}
              >
                <option value="">{t("No saved view selected")}</option>
                {data.views.map((view) => (
                  <option key={view.id} value={view.id}>
                    {view.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="space-y-1">
              <span>{t("View name")}</span>
              <Input
                aria-label={t("View name")}
                value={name}
                maxLength={64}
                onChange={(event) => setName(event.target.value)}
                disabled={busy}
              />
            </label>
            <Button
              variant="outline"
              disabled={busy || !name.trim()}
              onClick={() => void change("create")}
            >
              {t("Save new view")}
            </Button>
            <Button
              variant="outline"
              disabled={busy || !selected || !name.trim()}
              onClick={() => void change("update")}
            >
              {t("Update view")}
            </Button>
            <Button
              variant="outline"
              disabled={busy || !selected}
              onClick={() => void change("delete")}
            >
              {t("Delete view")}
            </Button>
          </div>
          <details>
            <summary>{t("Visible columns")}</summary>
            <div className="flex flex-wrap gap-4 py-2">
              {columns.map((column) => (
                <label key={column.key} className="flex items-center gap-2">
                  <input
                    type="checkbox"
                    aria-label={`${t("Show column")}: ${t(column.label)}`}
                    checked={settings.columns.includes(column.key)}
                    disabled={
                      busy ||
                      (settings.columns.length === 1 &&
                        settings.columns.includes(column.key))
                    }
                    onChange={(event) =>
                      onRestore({
                        ...settings,
                        columns: event.target.checked
                          ? columns
                              .filter(
                                (item) =>
                                  item.key === column.key ||
                                  settings.columns.includes(item.key),
                              )
                              .map((item) => item.key)
                          : settings.columns.filter(
                              (key) => key !== column.key,
                            ),
                      })
                    }
                  />
                  {t("Show column")}: {t(column.label)}
                </label>
              ))}
            </div>
          </details>
          <p className="text-xs text-muted-foreground">
            {t("The last saved selection restores after sign-in.")}
          </p>
        </>
      )}
    </section>
  );
}
