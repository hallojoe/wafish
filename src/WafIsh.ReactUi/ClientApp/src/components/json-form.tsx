import { useState } from "react";
import { useLocale } from "@/locales/i18n";
import { useApi, useWrite } from "@/hooks/api";
import { field, object, text, setField } from "@/api/client";
import type { JsonObject } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "@/components/ui/dialog";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  ErrorNotice,
  Field,
  JsonEditor,
  Panel,
  QueryState,
  JsonView,
  parseObject,
} from "@/components/workspace";
export type FormField = {
  key: string;
  label: string;
  type?: "text" | "number" | "checkbox" | "password";
  options?: string[];
};
export function ObjectFields({
  value,
  fields,
  onChange,
}: {
  value: JsonObject;
  fields: FormField[];
  onChange: (value: JsonObject) => void;
}) {
  const { t } = useLocale();
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      {fields.map((f) => (
        <Field key={f.key} label={f.label}>
          {f.options ? (
            <select
              className="native-select"
              value={text(field(value, f.key), "")}
              onChange={(e) => onChange(setField(value, f.key, e.target.value))}
            >
              {f.options.map((o) => (
                <option key={o} value={o}>
                  {t(o)}
                </option>
              ))}
            </select>
          ) : f.type === "checkbox" ? (
            <input
              type="checkbox"
              className="size-5 accent-primary"
              checked={field(value, f.key) === true}
              onChange={(e) =>
                onChange(setField(value, f.key, e.target.checked))
              }
            />
          ) : (
            <Input
              type={f.type ?? "text"}
              value={text(field(value, f.key), "")}
              onChange={(e) =>
                onChange(
                  setField(
                    value,
                    f.key,
                    f.type === "number"
                      ? Number(e.target.value)
                      : e.target.value,
                  ),
                )
              }
            />
          )}
        </Field>
      ))}
    </div>
  );
}
export function ObjectEditor({
  initial,
  title,
  path,
  method = "POST",
  fields = [],
  onSaved,
  onClose,
}: {
  initial: JsonObject;
  title: string;
  path: string;
  method?: string;
  fields?: FormField[];
  onSaved?: () => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState(JSON.stringify(initial, null, 2));
  const [error, setError] = useState<unknown>();
  const mutation = useWrite();
  const { t } = useLocale();
  let parsed: JsonObject | null = null;
  try {
    parsed = parseObject(draft);
  } catch {
    /* Show the editable JSON tab. */
  }
  return (
    <Dialog
      open
      onOpenChange={(v) => {
        if (!v && !mutation.isPending) onClose();
      }}
    >
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{t(title)}</DialogTitle>
          <DialogDescription>
            {t("This action changes gateway configuration.")}
          </DialogDescription>
        </DialogHeader>
        <Tabs defaultValue={fields.length ? "common" : "json"}>
          <TabsList>
            {fields.length > 0 && (
              <TabsTrigger value="common">{t("Common fields")}</TabsTrigger>
            )}
            <TabsTrigger value="json">{t("Advanced JSON")}</TabsTrigger>
          </TabsList>
          <TabsContent value="common">
            {parsed ? (
              <ObjectFields
                value={parsed}
                fields={fields}
                onChange={(value) => setDraft(JSON.stringify(value, null, 2))}
              />
            ) : (
              <p role="alert">{t("Invalid JSON")}</p>
            )}
          </TabsContent>
          <TabsContent value="json">
            <JsonEditor value={draft} onChange={setDraft} />
          </TabsContent>
        </Tabs>
        <ErrorNotice error={error} />
        <DialogFooter>
          <Button
            variant="outline"
            disabled={mutation.isPending}
            onClick={onClose}
          >
            {t("Cancel")}
          </Button>
          <Button
            disabled={mutation.isPending}
            onClick={async () => {
              try {
                await mutation.mutateAsync({
                  path,
                  method,
                  body: parseObject(draft),
                });
                onSaved?.();
                onClose();
              } catch (e) {
                setError(e);
              }
            }}
          >
            {t("Save changes")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
export function SettingsPanel({
  title,
  path,
  method = "POST",
  fields = [],
  transform = object,
}: {
  title: string;
  path: string;
  method?: string;
  fields?: FormField[];
  transform?: (value: unknown) => JsonObject;
}) {
  const query = useApi(path);
  const [editing, setEditing] = useState(false);
  const { t } = useLocale();
  return (
    <Panel
      title={title}
      actions={
        <Button
          variant="outline"
          disabled={!query.data}
          onClick={() => setEditing(true)}
        >
          {t("Edit")}
        </Button>
      }
    >
      <QueryState query={query}>
        <JsonView value={query.data} />
      </QueryState>
      {editing && (
        <ObjectEditor
          initial={transform(query.data)}
          title={title}
          path={path}
          method={method}
          fields={fields}
          onClose={() => setEditing(false)}
        />
      )}
    </Panel>
  );
}
