import { field, object, setField, text } from "@/api/client";
import type { JsonObject } from "@/api/client";
import { Input } from "@/components/ui/input";
import { Field, parseObject } from "@/components/workspace";
import { useLocale } from "@/locales/i18n";
export function schemaDefaults(schema: unknown): JsonObject {
  const result: JsonObject = {};
  for (const [name, property] of Object.entries(
    object(field(schema, "properties")),
  ))
    if (field(property, "default") !== undefined)
      result[name] = field(property, "default");
  return result;
}
export function SchemaFields({
  schema,
  value,
  onChange,
}: {
  schema: unknown;
  value: string;
  onChange: (value: string) => void;
}) {
  const { t } = useLocale();
  let parsed: JsonObject;
  try {
    parsed = parseObject(value);
  } catch {
    return <p role="alert">{t("Invalid JSON")}</p>;
  }
  const required = field(schema, "required") as string[] | undefined;
  function change(name: string, value: unknown) {
    onChange(JSON.stringify(setField(parsed, name, value), null, 2));
  }
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      {Object.entries(object(field(schema, "properties"))).map(
        ([name, property]) => {
          const type = field(property, "type");
          const choices = field(property, "enum");
          const current = field(parsed, name) ?? field(property, "default");
          const label = text(
            field(property, "title"),
            name.replace(/([A-Z])/g, " $1"),
          );
          if (!["boolean", "string", "integer", "number"].includes(text(type)))
            return null;
          return (
            <Field key={name} label={label}>
              {type === "boolean" ? (
                <input
                  type="checkbox"
                  className="size-5 accent-primary"
                  checked={current === true}
                  onChange={(e) => change(name, e.target.checked)}
                />
              ) : Array.isArray(choices) ? (
                <select
                  className="native-select"
                  value={text(current, "")}
                  onChange={(e) => change(name, e.target.value)}
                >
                  {choices.map((option) => (
                    <option key={text(option)}>{text(option)}</option>
                  ))}
                </select>
              ) : (
                <Input
                  type={type === "string" ? "text" : "number"}
                  required={required?.includes(name)}
                  min={field(property, "minimum") as number | undefined}
                  max={field(property, "maximum") as number | undefined}
                  step={type === "integer" ? 1 : "any"}
                  value={text(current, "")}
                  onChange={(e) =>
                    change(
                      name,
                      type === "string"
                        ? e.target.value
                        : Number(e.target.value),
                    )
                  }
                />
              )}
            </Field>
          );
        },
      )}
    </div>
  );
}
