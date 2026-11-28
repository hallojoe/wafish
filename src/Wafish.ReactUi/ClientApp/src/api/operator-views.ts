import { api } from "@/api/client";

export type ViewSettings = {
  search: string;
  filter: string;
  route: string;
  minimumStatus: number | null;
  historical: boolean;
  columns: string[];
};
export type OperatorView = {
  id: string;
  workspace: string;
  name: string;
  settings: ViewSettings;
  isDefault: boolean;
  revision: number;
};
export type OperatorViews = { available: boolean; views: OperatorView[] };
export const readViews = (workspace: string, signal: AbortSignal) =>
  api<OperatorViews>(`api/operator-views/${encodeURIComponent(workspace)}`, {
    signal,
  });
export const saveView = (
  workspace: string,
  view: Omit<OperatorView, "workspace">,
  signal: AbortSignal,
) =>
  api<OperatorView>(
    `api/operator-views/${encodeURIComponent(workspace)}/${view.id}`,
    {
      method: "PUT",
      signal,
      headers: { "X-Wafish-Operator-View": "1" },
      body: JSON.stringify({
        name: view.name,
        settings: view.settings,
        isDefault: view.isDefault,
        expectedRevision: view.revision,
      }),
    },
  );
export const deleteView = (
  workspace: string,
  view: OperatorView,
  signal: AbortSignal,
) =>
  api(
    `api/operator-views/${encodeURIComponent(workspace)}/${view.id}?expectedRevision=${view.revision}`,
    {
      method: "DELETE",
      signal,
      headers: { "X-Wafish-Operator-View": "1" },
    },
  );
