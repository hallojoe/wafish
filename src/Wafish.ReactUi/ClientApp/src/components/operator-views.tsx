import { useState } from "react";
import { write, idPath } from "@/api/client";
import { useApi } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Field,
  Confirm,
  ErrorNotice,
  QueryState,
} from "@/components/workspace";

export interface ViewFilters {
  historical: boolean;
  search: string;
  route: string;
  minimumStatus: number | null;
}
export interface SavedView {
  id: string;
  name: string;
  workspace: "logs" | "audit";
  filters: ViewFilters;
  columns: string[];
}
export const logColumns = [
  { key: "timestamp", label: "Time" },
  { key: "method", label: "Method" },
  { key: "path", label: "Path" },
  { key: "routeId", label: "Route" },
  { key: "statusCode", label: "Status" },
  { key: "elapsedMs", label: "Duration" },
];
export const auditColumns = [
  { key: "timestamp", label: "Time" },
  { key: "action", label: "Operation" },
  { key: "message", label: "Description" },
  { key: "operatorName", label: "Account" },
];

export function OperatorViews({
  workspace,
  filters,
  columns,
  onColumns,
  onApply,
}: {
  workspace: "logs" | "audit";
  filters: ViewFilters;
  columns: string[];
  onColumns: (columns: string[]) => void;
  onApply: (view: SavedView) => void;
}) {
  const query = useApi<{ available: boolean; views: SavedView[] }>(
    "api/operator-views",
  );
  const [selected, setSelected] = useState("");
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  const { t } = useLocale();
  if (query.data?.available === false) return null;
  const views =
    query.data?.views?.filter((view) => view.workspace === workspace) ?? [];
  const current = views.find((view) => view.id === selected);
  const choices = workspace === "logs" ? logColumns : auditColumns;
  return (
    <details className="mb-5">
      <summary className="cursor-pointer font-medium">
        {t("Saved views")}
      </summary>
      <QueryState query={query}>
        <div className="toolbar mt-3">
          <Field label="Saved view">
            <select
              className="native-select"
              value={selected}
              onChange={(event) => {
                setSelected(event.target.value);
                setName(
                  views.find((view) => view.id === event.target.value)?.name ??
                    "",
                );
                setError(undefined);
              }}
            >
              <option value="">{t("New view")}</option>
              {views.map((view) => (
                <option key={view.id} value={view.id}>
                  {view.name}
                </option>
              ))}
            </select>
          </Field>
          <Button
            variant="outline"
            disabled={!current || busy}
            onClick={() => {
              if (current) onApply(current);
            }}
          >
            {t("Load view")}
          </Button>
          <Field label="View name">
            <Input
              value={name}
              maxLength={64}
              onChange={(event) => setName(event.target.value)}
            />
          </Field>
          <Button
            disabled={!name.trim() || busy || (selected !== "" && !current)}
            onClick={async () => {
              setBusy(true);
              setError(undefined);
              try {
                const view = await write<SavedView>(
                  "api/operator-views" +
                    (current ? "/" + idPath(current.id) : ""),
                  current ? "PUT" : "POST",
                  { name: name.trim(), workspace, filters, columns },
                );
                setSelected(view.id);
                setName(view.name);
                await query.refetch();
              } catch (error) {
                setError(error);
              } finally {
                setBusy(false);
              }
            }}
          >
            {t("Save view")}
          </Button>
          {current && (
            <Confirm
              title="Delete saved view"
              onConfirm={async () => {
                await write(
                  "api/operator-views/" + idPath(current.id),
                  "DELETE",
                );
                setSelected("");
                setName("");
                await query.refetch();
              }}
            >
              {t("Delete view")}
            </Confirm>
          )}
        </div>
        <fieldset className="flex flex-wrap gap-4 mt-3">
          <legend>{t("Visible columns")}</legend>
          {choices.map((column) => (
            <label key={column.key} className="flex items-center gap-2">
              <input
                type="checkbox"
                checked={columns.includes(column.key)}
                disabled={columns.length === 1 && columns.includes(column.key)}
                onChange={(event) =>
                  onColumns(
                    event.target.checked
                      ? [...columns, column.key]
                      : columns.filter((key) => key !== column.key),
                  )
                }
              />
              {t("Show")} {t(column.label)}
            </label>
          ))}
        </fieldset>
        <ErrorNotice error={error} />
      </QueryState>
    </details>
  );
}
