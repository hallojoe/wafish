import { useState } from "react";
import {
  OperatorViews,
  logColumns,
  auditColumns,
} from "@/components/operator-views";
import { Link } from "react-router";
import {
  Activity,
  ArrowUpRight,
  CircleDot,
  Cpu,
  Network,
  Pause,
  Play,
  RefreshCw,
} from "lucide-react";
import { api, field, rows, text, object, idPath } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLive } from "@/hooks/live";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import {
  PageTitle,
  Panel,
  QueryState,
  Records,
  Inspect,
  Confirm,
  JsonView,
  ErrorNotice,
  Field,
} from "@/components/workspace";
function LiveControl({
  state,
  paused,
  onChange,
}: {
  state: string;
  paused: boolean;
  onChange: (value: boolean) => void;
}) {
  const { t } = useLocale();
  return (
    <div className="toolbar">
      <Badge variant="outline" role="status">
        <span
          className={`status-dot ${state === "Connected" ? "online" : ""}`}
        />
        {t(state)}
      </Badge>
      <Button variant="outline" size="sm" onClick={() => onChange(!paused)}>
        {paused ? <Play /> : <Pause />}
        {t(paused ? "Resume" : "Pause")}
      </Button>
    </div>
  );
}
export function OverviewPage() {
  const [paused, setPaused] = useState(false);
  const state = useLive("overview", paused);
  const query = useApi("api/overview/snapshot", true, paused ? false : 15000);
  const changes = useApi("api/config/history?pageSize=5");
  const { t } = useLocale();
  const metrics = [
    { key: "routeCount", title: "Routes", icon: Network, unit: "" },
    {
      key: "currentQps",
      title: "Requests per second",
      icon: Activity,
      unit: "/s",
    },
    { key: "cpuUsage", title: "CPU", icon: Cpu, unit: "%" },
    { key: "memoryMb", title: "Memory", icon: CircleDot, unit: "MB" },
  ];
  return (
    <>
      <PageTitle title="Overview" subtitle="Routing at a glance">
        <LiveControl state={state} paused={paused} onChange={setPaused} />
      </PageTitle>
      <QueryState query={query}>
        <div className="metrics-grid">
          {metrics.map((metric) => (
            <div key={metric.key} className="metric-card">
              <div className="flex items-center justify-between text-muted-foreground">
                <span>{t(metric.title)}</span>
                <metric.icon className="size-4" />
              </div>
              <p className="metric-value">
                {text(field(query.data, metric.key))}
                <span>{metric.unit}</span>
              </p>
            </div>
          ))}
        </div>
        <div className="grid gap-6 lg:grid-cols-3 mt-6">
          <div className="lg:col-span-2 space-y-6">
            <Panel
              title="Cluster health"
              actions={
                <Link className="inline-link" to="/observe/destinations">
                  {t("Details")}
                  <ArrowUpRight className="size-4" />
                </Link>
              }
            >
              <div className="grid grid-cols-3 gap-3">
                {["Healthy", "Unknown", "Unhealthy"].map((label, i) => (
                  <div key={label}>
                    <p
                      className={`text-3xl font-semibold ${i === 0 ? "text-emerald-700 dark:text-emerald-400" : ""}`}
                    >
                      {text(field(query.data, label.toLowerCase() + "Count"))}
                    </p>
                    <p className="text-sm text-muted-foreground mt-2">
                      {t(label)}
                    </p>
                  </div>
                ))}
              </div>
              <p className="text-xs text-muted-foreground mt-5">
                {t("Cluster count")}: {text(field(query.data, "clusterCount"))}
              </p>
            </Panel>
            <Panel
              title="Recent activity"
              actions={
                <Link className="inline-link" to="/observe/logs">
                  {t("View logs")}
                  <ArrowUpRight className="size-4" />
                </Link>
              }
            >
              <Records
                search={false}
                data={rows(field(query.data, "topErrorRoutes"))}
                columns={[
                  { key: "routeId", label: "Route" },
                  { key: "errorCount", label: "Errors" },
                  { key: "errorRate", label: "Error rate" },
                ]}
              />
            </Panel>
          </div>
          <Panel
            title="Configuration changes"
            actions={
              <Link className="inline-link" to="/configuration/history">
                {t("View history")}
              </Link>
            }
          >
            <QueryState query={changes}>
              <div className="space-y-5">
                {rows(changes.data).length ? (
                  rows(changes.data).map((row, i) => (
                    <div className="timeline-item" key={i}>
                      <span className="timeline-dot" />
                      <p className="text-sm font-medium break-words">
                        {text(field(row, "description"))}
                      </p>
                      <p className="text-xs text-muted-foreground mt-2">
                        {text(field(row, "timestamp"))}
                      </p>
                    </div>
                  ))
                ) : (
                  <p className="text-sm text-muted-foreground">
                    {t("No configuration changes yet.")}
                  </p>
                )}
              </div>
            </QueryState>
          </Panel>
        </div>
        <div className="mt-6">
          <Panel title="Configuration health">
            <Inspect value={field(query.data, "proxyConfigHealth")} />
            <ApplyErrors />
          </Panel>
        </div>
      </QueryState>
    </>
  );
}
function ApplyErrors() {
  const query = useApi("api/config/apply-errors");
  return (
    <QueryState query={query}>
      <Inspect value={query.data} label="Review details" />
    </QueryState>
  );
}
export function TrafficPage() {
  const [paused, setPaused] = useState(false);
  const [minutes, setMinutes] = useState(60);
  const routes = useApi("api/routes");
  const state = useLive(
    "traffic",
    paused,
    rows(routes.data).map((r) => text(field(r, "routeId"))),
  );
  const query = useApi(
    `api/traffic/stats?minutes=${minutes}`,
    true,
    paused ? false : 5000,
  );
  const { t } = useLocale();
  const buckets = rows(field(query.data, "buckets"));
  const max = Math.max(1, ...buckets.map((b) => Number(field(b, "requests"))));
  return (
    <>
      <PageTitle title="Traffic">
        <Field label="Window">
          <select
            className="native-select"
            value={minutes}
            onChange={(e) => setMinutes(Number(e.target.value))}
          >
            {[15, 60, 240, 1440].map((value) => (
              <option key={value} value={value}>
                {value} {t("minutes")}
              </option>
            ))}
          </select>
        </Field>
        <LiveControl state={state} paused={paused} onChange={setPaused} />
      </PageTitle>
      <QueryState query={query}>
        <div className="metrics-grid">
          {[
            ["totalRequests", "Requests"],
            ["errorCount", "Errors"],
            ["avgLatency", "Average latency"],
            ["successRate", "Success rate"],
          ].map(([key, label]) => (
            <div className="metric-card" key={key}>
              <p className="text-muted-foreground">{t(label)}</p>
              <p className="metric-value">
                {text(field(query.data, key))}
                <span>
                  {key === "avgLatency"
                    ? "ms"
                    : key === "successRate"
                      ? "%"
                      : ""}
                </span>
              </p>
            </div>
          ))}
        </div>
        <div className="space-y-6 mt-6">
          <Panel title="Requests">
            <div
              className="traffic-chart"
              role="img"
              aria-label={t("Requests over time")}
            >
              {buckets.map((b, i) => (
                <div
                  key={i}
                  title={`${text(field(b, "time"))}: ${text(field(b, "requests"))}`}
                  style={{
                    height: `${Math.max(1, (Number(field(b, "requests")) / max) * 100)}%`,
                  }}
                />
              ))}
            </div>
            <details className="mt-4">
              <summary>{t("View data")}</summary>
              <Records
                data={buckets}
                columns={[
                  { key: "time", label: "Time" },
                  { key: "requests", label: "Requests" },
                  { key: "errors", label: "Errors" },
                  { key: "avgLatency", label: "Average latency" },
                ]}
              />
            </details>
          </Panel>
          <Panel title="Routes">
            <Records
              data={rows(field(query.data, "topRoutes"))}
              columns={[
                { key: "name", label: "Route" },
                { key: "count", label: "Requests" },
                { key: "avgLatency", label: "Average latency" },
              ]}
            />
          </Panel>
          <Panel title="Details">
            <JsonView
              value={{
                percentiles: field(query.data, "percentiles"),
                statusCodes: field(query.data, "statusCodes"),
                topClusters: field(query.data, "topClusters"),
              }}
            />
          </Panel>
        </div>
      </QueryState>
    </>
  );
}
export function LogsPage({ audit = false }: { audit?: boolean }) {
  const [columnSettings, setColumnSettings] = useState({
    logs: logColumns.map((column) => column.key),
    audit: auditColumns.map((column) => column.key),
  });
  const workspace = audit ? "audit" : "logs";
  const columns = columnSettings[workspace];
  const setColumns = (value: string[]) =>
    setColumnSettings((current) => ({ ...current, [workspace]: value }));
  const [page, setPage] = useState(1);
  const [historical, setHistorical] = useState(false);
  const [filter, setFilter] = useState("");
  const [route, setRoute] = useState("");
  const [status, setStatus] = useState("");
  const [detail, setDetail] = useState<unknown>();
  const [error, setError] = useState<unknown>();
  const [paused, setPaused] = useState(false);
  const path = audit
    ? `api/audit-logs?page=${page}&pageSize=50&action=${encodeURIComponent(filter)}`
    : historical
      ? `api/logs/history?page=${page}&pageSize=50&keyword=${encodeURIComponent(filter)}&routeId=${encodeURIComponent(route)}${status ? "&statusCodeMin=" + status : ""}`
      : `api/logs?page=${page}&pageSize=50`;
  const query = useApi(
    path,
    true,
    paused || historical || audit ? false : 5000,
  );
  const mutation = useWrite();
  const { t } = useLocale();
  const data = rows(query.data);
  return (
    <>
      <PageTitle title={audit ? "Audit" : "Logs"}>
        <Button variant="outline" onClick={() => void query.refetch()}>
          <RefreshCw />
          {t("Refresh")}
        </Button>
        {!audit && (
          <>
            <Button variant="outline" onClick={() => setPaused(!paused)}>
              {t(paused ? "Resume" : "Pause")}
            </Button>
            <Confirm
              title="Clear logs"
              onConfirm={() =>
                mutation.mutateAsync({ path: "api/logs", method: "DELETE" })
              }
            >
              {t("Clear logs")}
            </Confirm>
          </>
        )}
      </PageTitle>
      <Panel title={audit ? "Audit" : "Logs"}>
        <OperatorViews
          key={workspace}
          workspace={workspace}
          filters={{
            historical: !audit && historical,
            search: filter,
            route: audit ? "" : route,
            minimumStatus: !audit && status ? Number(status) : null,
          }}
          columns={columns}
          onColumns={setColumns}
          onApply={(view) => {
            setHistorical(view.filters.historical);
            setFilter(view.filters.search);
            setRoute(view.filters.route);
            setStatus(
              view.filters.minimumStatus === null
                ? ""
                : String(view.filters.minimumStatus),
            );
            setColumns(view.columns);
            setPage(1);
          }}
        />
        <div className="toolbar mb-5">
          {!audit && (
            <Field label="Source">
              <select
                className="native-select"
                value={historical ? "history" : "live"}
                onChange={(e) => {
                  setHistorical(e.target.value === "history");
                  setPage(1);
                }}
              >
                <option value="live">{t("Live")}</option>
                <option value="history">{t("History")}</option>
              </select>
            </Field>
          )}
          {(audit || historical) && (
            <Field label={audit ? "Operation" : "Search"}>
              <Input
                value={filter}
                onChange={(e) => {
                  setFilter(e.target.value);
                  setPage(1);
                }}
              />
            </Field>
          )}
          {historical && !audit && (
            <>
              <Field label="Route">
                <Input
                  value={route}
                  onChange={(e) => {
                    setRoute(e.target.value);
                    setPage(1);
                  }}
                />
              </Field>
              <Field label="Minimum status">
                <Input
                  type="number"
                  value={status}
                  onChange={(e) => {
                    setStatus(e.target.value);
                    setPage(1);
                  }}
                />
              </Field>
            </>
          )}
        </div>
        <QueryState query={query}>
          <Records
            data={data}
            columns={(audit
              ? [
                  { key: "timestamp", label: "Time" },
                  { key: "action", label: "Operation" },
                  { key: "message", label: "Description" },
                  { key: "operatorName", label: "Account" },
                ]
              : [
                  { key: "timestamp", label: "Time" },
                  { key: "method", label: "Method" },
                  {
                    key: "path",
                    label: "Path",
                    render: (row: unknown) =>
                      text(
                        field(row, "upstreamPath") ??
                          field(row, "path") ??
                          field(row, "message"),
                      ),
                  },
                  { key: "routeId", label: "Route" },
                  { key: "statusCode", label: "Status" },
                  { key: "elapsedMs", label: "Duration" },
                ]
            ).filter((column) => columns.includes(column.key))}
            actions={(row) => (
              <Button
                variant="ghost"
                size="sm"
                onClick={async () => {
                  setError(undefined);
                  try {
                    setDetail(
                      historical && !audit
                        ? await api(
                            `api/logs/detail/${idPath(text(field(row, "id")))}`,
                          )
                        : row,
                    );
                  } catch (e) {
                    setError(e);
                  }
                }}
              >
                {t("Details")}
              </Button>
            )}
          />
          <div className="toolbar justify-end mt-4">
            <Button
              variant="outline"
              disabled={page === 1}
              onClick={() => setPage((p) => p - 1)}
            >
              {t("Previous")}
            </Button>
            <span>
              {t("Page")} {page}
            </span>
            <Button
              variant="outline"
              disabled={data.length < 50}
              onClick={() => setPage((p) => p + 1)}
            >
              {t("Next")}
            </Button>
          </div>
        </QueryState>
        <ErrorNotice error={error} />
        {detail !== undefined && (
          <div className="mt-5">
            <div className="toolbar justify-between">
              <h2>{t("Request details")}</h2>
              <Button variant="ghost" onClick={() => setDetail(undefined)}>
                {t("Close")}
              </Button>
            </div>
            <JsonView value={detail} />
          </div>
        )}
      </Panel>
    </>
  );
}
export function DestinationsPage() {
  const query = useApi("api/clusters", true, 10000);
  const circuits = useApi("api/circuit-breaker/status", true, 10000);
  const config = useApi("api/circuit-breaker/config");
  const mutation = useWrite();
  const { t } = useLocale();
  const data = rows(query.data).flatMap((c) =>
    rows(field(c, "destinations")).map((d) => ({
      ...d,
      clusterId: field(c, "clusterId"),
    })),
  );
  return (
    <>
      <PageTitle title="Destinations">
        <Confirm
          title="Reset circuits"
          onConfirm={() =>
            mutation.mutateAsync({
              path: "api/circuit-breaker/reset",
              method: "POST",
            })
          }
        >
          {t("Reset circuits")}
        </Confirm>
      </PageTitle>
      <div className="space-y-6">
        <Panel title="Destination health">
          <QueryState query={query}>
            <Records
              data={data}
              columns={[
                { key: "clusterId", label: "Cluster" },
                { key: "name", label: "Name" },
                { key: "address", label: "Destination address" },
                { key: "activeHealth", label: "Active health" },
                { key: "passiveHealth", label: "Passive health" },
              ]}
              actions={(row) => <Inspect value={row} />}
            />
          </QueryState>
        </Panel>
        <Panel title="Circuits">
          <QueryState query={circuits}>
            <Records
              data={
                Array.isArray(circuits.data)
                  ? rows(circuits.data)
                  : Object.entries(object(circuits.data)).map(
                      ([name, value]) => ({ name, ...object(value) }),
                    )
              }
              columns={[
                { key: "name", label: "Name" },
                { key: "state", label: "State" },
              ]}
              actions={(row) => <Inspect value={row} />}
            />
          </QueryState>
          <Inspect value={config.data} label="Circuit configuration" />
        </Panel>
      </div>
    </>
  );
}
export function ResourcesPage() {
  const query = useApi("api/plugin-resources", true, 10000);
  return (
    <>
      <PageTitle title="Resources" />
      <Panel title="Resources">
        <QueryState query={query}>
          <Records
            data={rows(query.data)}
            columns={[
              { key: "pluginId", label: "Plugin" },
              { key: "requestCount", label: "Requests" },
              { key: "errorCount", label: "Errors" },
              { key: "overallHealth", label: "Health" },
              { key: "memoryBytes", label: "Memory" },
            ]}
            actions={(row) => <Inspect value={row} />}
          />
          <JsonView value={field(query.data, "totals")} />
        </QueryState>
      </Panel>
    </>
  );
}
