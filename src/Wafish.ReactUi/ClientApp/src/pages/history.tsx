import { useState } from "react";
import { api, field, idPath, rows, text } from "@/api/client";
import { useApi, useWrite } from "@/hooks/api";
import { useLocale } from "@/locales/i18n";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  PageTitle,
  Panel,
  Records,
  QueryState,
  Confirm,
  JsonView,
  ErrorNotice,
  Field,
} from "@/components/workspace";
export function HistoryPage() {
  const [page, setPage] = useState(1);
  const [filter, setFilter] = useState("");
  const [description, setDescription] = useState("");
  const [diff, setDiff] = useState<unknown>();
  const [error, setError] = useState<unknown>();
  const { t } = useLocale();
  const mutation = useWrite();
  const query = useApi(
    `api/config/history?page=${page}&pageSize=50&q=${encodeURIComponent(filter)}`,
  );
  return (
    <>
      <PageTitle title="History">
        <Confirm
          title="Clear history"
          onConfirm={() =>
            mutation.mutateAsync({
              path: "api/config/history",
              method: "DELETE",
            })
          }
        >
          {t("Clear history")}
        </Confirm>
      </PageTitle>
      <div className="space-y-6">
        <Panel title="Create snapshot">
          <div className="toolbar items-end">
            <div className="grow">
              <Field label="Snapshot description">
                <Input
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                />
              </Field>
            </div>
            <Button
              disabled={mutation.isPending}
              onClick={async () => {
                try {
                  await mutation.mutateAsync({
                    path: "api/config/snapshot",
                    method: "POST",
                    body: { description },
                  });
                  setDescription("");
                } catch (e) {
                  setError(e);
                }
              }}
            >
              {t("Create snapshot")}
            </Button>
          </div>
        </Panel>
        <Panel title="History">
          <Field label="Search">
            <Input
              value={filter}
              onChange={(e) => {
                setFilter(e.target.value);
                setPage(1);
              }}
            />
          </Field>
          <div className="mt-5">
            <QueryState query={query}>
              <Records
                viewKey="configuration-history"
                search={false}
                data={rows(query.data)}
                columns={[
                  { key: "timestamp", label: "Time" },
                  { key: "description", label: "Description" },
                  { key: "routeCount", label: "Routes" },
                  { key: "clusterCount", label: "Clusters" },
                ]}
                actions={(row) => (
                  <>
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={async () => {
                        try {
                          setDiff(
                            await api(
                              `api/config/diff/${idPath(text(field(row, "versionId")))}`,
                            ),
                          );
                          setError(undefined);
                        } catch (e) {
                          setError(e);
                        }
                      }}
                    >
                      {t("Compare")}
                    </Button>
                    <Confirm
                      title="Rollback"
                      description={text(field(row, "description"))}
                      onConfirm={() =>
                        mutation.mutateAsync({
                          path: `api/config/rollback/${idPath(text(field(row, "versionId")))}`,
                          method: "POST",
                        })
                      }
                    >
                      {t("Rollback")}
                    </Confirm>
                  </>
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
                  disabled={rows(query.data).length < 50}
                  onClick={() => setPage((p) => p + 1)}
                >
                  {t("Next")}
                </Button>
              </div>
            </QueryState>
          </div>
        </Panel>
        <ErrorNotice error={error} />
        {diff !== undefined && (
          <Panel title="Compare">
            <JsonView value={diff} />
          </Panel>
        )}
      </div>
    </>
  );
}
