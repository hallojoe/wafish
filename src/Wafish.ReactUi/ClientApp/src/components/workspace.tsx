import { SavedViews } from "@/components/saved-views";
import { cloneElement, isValidElement, useId, useState } from "react";
import type { ReactNode, ReactElement } from "react";
import {
  AlertCircle,
  ArrowUpRight,
  Search,
  RefreshCw,
  Inbox,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
} from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { useLocale } from "@/locales/i18n";
import { ApiError, field, text, object } from "@/api/client";
import type { JsonObject } from "@/api/client";

export function PageTitle({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle?: string;
  children?: ReactNode;
}) {
  const { t } = useLocale();
  return (
    <div className="page-heading">
      <div>
        <p className="eyebrow">{t("Gateway workspace")}</p>
        <h1>{t(title)}</h1>
        {subtitle && (
          <p className="text-muted-foreground mt-2">{t(subtitle)}</p>
        )}
      </div>
      <div className="toolbar">{children}</div>
    </div>
  );
}
export function Panel({
  title,
  children,
  actions,
}: {
  title: string;
  children: ReactNode;
  actions?: ReactNode;
}) {
  const { t } = useLocale();
  return (
    <Card className="gap-0 overflow-hidden py-0">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b px-5 py-4">
        <h2 className="font-semibold">{t(title)}</h2>
        {actions}
      </div>
      <CardContent className="p-5">{children}</CardContent>
    </Card>
  );
}
export function Field({
  label,
  children,
}: {
  label: string;
  children: ReactNode;
}) {
  const { t } = useLocale();
  const id = useId();
  return (
    <div className="grid min-w-0 gap-2 text-sm font-medium">
      <label id={id + "-label"} htmlFor={id}>
        {t(label)}
      </label>
      {isValidElement(children)
        ? cloneElement(
            children as ReactElement<{
              id?: string;
              "aria-labelledby"?: string;
            }>,
            { id, "aria-labelledby": id + "-label" },
          )
        : children}
    </div>
  );
}
export function JsonEditor({
  value,
  onChange,
  label = "Advanced JSON",
}: {
  value: string;
  onChange: (value: string) => void;
  label?: string;
}) {
  const { t } = useLocale();
  const id = useId();
  return (
    <div className="grid gap-2">
      <label htmlFor={id} className="text-sm font-medium">
        {t(label)}
      </label>
      <Textarea
        id={id}
        spellCheck={false}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="json-editor"
      />
    </div>
  );
}
export function parseObject(value: string): JsonObject {
  const parsed: unknown = JSON.parse(value);
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed))
    throw new Error("JSON must contain an object.");
  return object(parsed);
}
export function JsonView({ value }: { value: unknown }) {
  return (
    <textarea
      className="json-view"
      aria-label="JSON data"
      readOnly
      rows={10}
      value={JSON.stringify(value, null, 2) ?? ""}
    />
  );
}
export function ErrorNotice({
  error,
  draft = true,
  title,
}: {
  error: unknown;
  draft?: boolean;
  title?: string;
}) {
  const { t } = useLocale();
  if (!error) return null;
  const apiError = error instanceof ApiError ? error : null;
  const status = text(
    field(apiError?.result, "outcome") ?? field(apiError?.result, "status"),
    "",
  );
  const kind =
    /Activation/i.test(status) || status === "4"
      ? "Activation failure"
      : /Storage/i.test(status) || status === "3" || apiError?.status === 503
        ? "Storage failure"
        : apiError?.status === 409
          ? "Version conflict"
          : apiError?.status === 400
            ? "Validation rejected"
            : "Save failed";
  return (
    <div role="alert" className="error-notice">
      <AlertCircle className="size-4 shrink-0" />
      <div className="min-w-0 space-y-2">
        <p className="font-semibold">{t(title ?? kind)}</p>
        <p>{t(error instanceof Error ? error.message : text(error))}</p>
        {draft && <p>{t("Your draft is preserved.")}</p>}
        {apiError?.status === 409 && (
          <p>{t("Reload and compare before saving.")}</p>
        )}
        {kind === "Activation failure" && (
          <p>{t("Restart the host before another update.")}</p>
        )}
        {["persistedVersion", "activeVersion"].map(
          (key, i) =>
            field(apiError?.result, key) != null && (
              <p key={key}>
                {t(i === 0 ? "Stored revision" : "Active revision")}:{" "}
                {text(field(apiError?.result, key))}
              </p>
            ),
        )}
        {apiError && (
          <details>
            <summary>{t("Review details")}</summary>
            <JsonView value={apiError.result} />
          </details>
        )}
      </div>
    </div>
  );
}
export function QueryState({
  query,
  children,
}: {
  query: { isPending: boolean; error: unknown; refetch: () => unknown };
  children: ReactNode;
}) {
  const { t } = useLocale();
  if (query.isPending)
    return (
      <div role="status" aria-label={t("Loading")} className="space-y-4">
        <Skeleton className="h-12 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  if (query.error)
    return (
      <div className="space-y-3">
        <ErrorNotice
          error={query.error}
          draft={false}
          title="Unable to load data"
        />
        <Button variant="outline" onClick={() => void query.refetch()}>
          <RefreshCw />
          {t("Try again")}
        </Button>
      </div>
    );
  return children;
}
export function StateBadge({ value }: { value: unknown }) {
  const { t } = useLocale();
  return (
    <Badge
      variant={value === false ? "secondary" : "outline"}
      className={value === true ? "status-good" : ""}
    >
      {t(
        value === true ? "Enabled" : value === false ? "Disabled" : text(value),
      )}
    </Badge>
  );
}
export function Empty() {
  const { t } = useLocale();
  return (
    <div className="empty">
      <Inbox className="size-8" />
      <p className="font-medium text-foreground">{t("No results")}</p>
      <p>{t("No matching records. Try another search.")}</p>
    </div>
  );
}
export type Column = {
  key: string;
  label: string;
  render?: (row: JsonObject) => ReactNode;
};
export function Records({
  data,
  columns,
  actions,
  search = true,
  viewKey,
  visibleColumns,
  searchValue,
  onSearchChange,
}: {
  data: JsonObject[];
  columns: Column[];
  actions?: (row: JsonObject) => ReactNode;
  search?: boolean;
  viewKey?: string;
  visibleColumns?: string[];
  searchValue?: string;
  onSearchChange?: (value: string) => void;
}) {
  const [localFilter, setLocalFilter] = useState("");
  const [chosenColumns, setChosenColumns] = useState<string[]>();
  const filter = search ? (searchValue ?? localFilter) : "";
  const setFilter = onSearchChange ?? setLocalFilter;
  const selectedColumns =
    visibleColumns ?? chosenColumns ?? columns.map((column) => column.key);
  const matchingColumns = columns.filter((column) =>
    selectedColumns.includes(column.key),
  );
  const displayedColumns = matchingColumns.length ? matchingColumns : columns;
  const { t } = useLocale();
  const [page, setPage] = useState(0);
  const filtered = data.filter((row) =>
    JSON.stringify(row).toLowerCase().includes(filter.toLowerCase()),
  );
  const visible = filtered.slice(page * 25, (page + 1) * 25);
  return (
    <div className="space-y-4">
      {viewKey && (
        <SavedViews
          key={viewKey}
          workspace={viewKey}
          columns={columns}
          settings={{
            search: filter,
            filter: "",
            route: "",
            minimumStatus: null,
            historical: false,
            columns: displayedColumns.map((column) => column.key),
          }}
          onRestore={(settings) => {
            setFilter(settings.search);
            setChosenColumns(settings.columns);
            setPage(0);
          }}
        />
      )}
      {search && (
        <div className="relative max-w-sm">
          <Search className="absolute left-3 top-3 size-4 text-muted-foreground" />
          <Input
            maxLength={256}
            aria-label={t("Filter records")}
            placeholder={t("Search")}
            className="pl-9"
            value={filter}
            onChange={(e) => {
              setFilter(e.target.value);
              setPage(0);
            }}
          />
        </div>
      )}
      {filtered.length === 0 ? (
        <Empty />
      ) : (
        <div className="table-scroll">
          <table>
            <thead>
              <tr>
                {displayedColumns.map((col) => (
                  <th key={col.key}>{t(col.label)}</th>
                ))}
                {actions && <th>{t("Actions")}</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((row, index) => (
                <tr
                  key={text(
                    field(row, "id") ??
                      field(row, "routeId") ??
                      field(row, "clusterId") ??
                      field(row, "versionId"),
                    String(index),
                  )}
                >
                  {displayedColumns.map((col) => (
                    <td key={col.key}>
                      {col.render ? col.render(row) : text(field(row, col.key))}
                    </td>
                  ))}
                  {actions && (
                    <td>
                      <div className="flex flex-wrap gap-2">{actions(row)}</div>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {filtered.length > 25 && (
        <div className="toolbar justify-end">
          <Button
            variant="outline"
            disabled={page === 0}
            onClick={() => setPage((p) => p - 1)}
          >
            {t("Previous")}
          </Button>
          <span>
            {t("Page")} {page + 1}
          </span>
          <Button
            variant="outline"
            disabled={(page + 1) * 25 >= filtered.length}
            onClick={() => setPage((p) => p + 1)}
          >
            {t("Next")}
          </Button>
        </div>
      )}
    </div>
  );
}
export function Confirm({
  title,
  description,
  children,
  onConfirm,
  disabled = false,
}: {
  title: string;
  description?: string;
  children: ReactNode;
  onConfirm: () => Promise<unknown>;
  disabled?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  const { t } = useLocale();
  return (
    <>
      <Button
        variant="outline"
        size="sm"
        disabled={disabled}
        onClick={() => {
          setOpen(true);
          setError(undefined);
        }}
      >
        {children}
      </Button>
      <Dialog
        open={open}
        onOpenChange={(v) => {
          if (!busy) setOpen(v);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t(title)}</DialogTitle>
            <DialogDescription>
              {description ?? t("This action changes gateway configuration.")}
            </DialogDescription>
          </DialogHeader>
          <ErrorNotice error={error} />
          <DialogFooter>
            <Button
              variant="outline"
              disabled={busy}
              onClick={() => setOpen(false)}
            >
              {t("Cancel")}
            </Button>
            <Button
              disabled={busy}
              onClick={async () => {
                setBusy(true);
                try {
                  await onConfirm();
                  setOpen(false);
                } catch (e) {
                  setError(e);
                } finally {
                  setBusy(false);
                }
              }}
            >
              {t("Confirm")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
export function Inspect({
  value,
  label = "Details",
}: {
  value: unknown;
  label?: string;
}) {
  const [open, setOpen] = useState(false);
  const { t } = useLocale();
  return (
    <>
      <Button size="sm" variant="ghost" onClick={() => setOpen(true)}>
        {t(label)}
        <ArrowUpRight className="size-3" />
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-3xl">
          <DialogHeader>
            <DialogTitle>{t(label)}</DialogTitle>
            <DialogDescription>{t("Read-only")}</DialogDescription>
          </DialogHeader>
          <JsonView value={value} />
        </DialogContent>
      </Dialog>
    </>
  );
}
