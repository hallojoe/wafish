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
          if (
            type === "array" &&
            field(field(property, "items"), "type") === "object"
          ) {
            const items = Array.isArray(current) ? current : [];
            const itemSchema = field(property, "items");
            return (
              <fieldset key={name} className="space-y-3 sm:col-span-2">
                <legend>{label}</legend>
                {items.map((item, index) => (
                  <div key={index} className="space-y-2 rounded border p-3">
                    <SchemaFields
                      schema={itemSchema}
                      value={JSON.stringify(item)}
                      onChange={(next) => {
                        const updated = [...items];
                        updated[index] = JSON.parse(next);
                        change(name, updated);
                      }}
                    />
                    <button
                      type="button"
                      className="native-button"
                      onClick={() =>
                        change(
                          name,
                          items.filter((_, i) => i !== index),
                        )
                      }
                    >
                      Remove {label} {index + 1}
                    </button>
                  </div>
                ))}
                <button
                  type="button"
                  className="native-button"
                  disabled={
                    items.length >= Number(field(property, "maxItems") ?? 32)
                  }
                  onClick={() =>
                    change(name, [...items, schemaDefaults(itemSchema)])
                  }
                >
                  Add {label}
                </button>
              </fieldset>
            );
          }
          if (
            !["boolean", "string", "integer", "number"].includes(text(type)) &&
            !(
              type === "array" &&
              field(field(property, "items"), "type") === "string"
            )
          )
            return null;
          return (
            <Field key={name} label={label}>
              {type === "array" ? (
                <textarea
                  className="native-input min-h-24"
                  aria-label={label}
                  onBlur={(e) =>
                    change(
                      name,
                      e.target.value
                        .split("\n")
                        .map((item) => item.trim())
                        .filter(Boolean),
                    )
                  }
                  value={Array.isArray(current) ? current.join("\n") : ""}
                  onChange={(e) => change(name, e.target.value.split("\n"))}
                />
              ) : type === "boolean" ? (
                <input
                  type="checkbox"
                  aria-label={label}
                  className="size-5 accent-primary"
                  checked={current === true}
                  onChange={(e) => change(name, e.target.checked)}
                />
              ) : Array.isArray(choices) ? (
                <select
                  className="native-select"
                  aria-label={label}
                  value={text(current, "")}
                  onChange={(e) =>
                    change(
                      name,
                      type === "integer" || type === "number"
                        ? Number(e.target.value)
                        : e.target.value,
                    )
                  }
                >
                  {choices.map((option) => (
                    <option key={text(option)}>{text(option)}</option>
                  ))}
                </select>
              ) : (
                <Input
                  type={type === "string" ? "text" : "number"}
                  aria-label={label}
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
              {field(property, "description") != null && (
                <p className="text-sm text-muted-foreground">
                  {text(field(property, "description"))}
                </p>
              )}
            </Field>
          );
        },
      )}
    </div>
  );
}
