import { field, object, text } from "@/api/client";
import type { JsonObject } from "@/api/client";
import { Field, parseObject } from "@/components/workspace";
import { useLocale } from "@/locales/i18n";

export function ExternalLoginEditor({ schema, value, onChange }: {
  schema: unknown;
  value: string;
  onChange: (value: string) => void;
}) {
  const { t } = useLocale();
  let config: JsonObject;
  try { config = parseObject(value); }
  catch { return <p role="alert">{t("Invalid JSON")}</p>; }
  const sites = object(field(schema, "x-wafish-api-targets"));
  const site = text(config.site, "");
  const mode = text(config.mode, "page");
  const target = text(config.apiTarget, "");
  const targets = Array.isArray(sites[site]) ? sites[site] as string[] : [];
  const available = Object.keys(sites);
  function change(name: string, next: string) {
    const updated: JsonObject = { ...config, mode, [name]: next };
    if (name === "site" || (name === "mode" && next === "page") || (name === "apiTarget" && !next)) delete updated.apiTarget;
    onChange(JSON.stringify(updated, null, 2));
  }
  return <div className="grid gap-4">
    <p>{t("Providers and API destinations are configured in host settings. Changes require a gateway restart.")}</p>
    {available.length === 0 && <p role="alert">{t("Configure an external login site in host settings first.")}</p>}
    <Field label="Site">
      <select className="native-select" aria-label={t("Site")} value={site} onChange={event => change("site", event.target.value)}>
        <option value="">{t("Select site")}</option>
        {site && !available.includes(site) && <option value={site}>{site} ({t("unavailable")})</option>}
        {available.map(id => <option key={id} value={id}>{id}</option>)}
      </select>
    </Field>
    <Field label="Request type">
      <select className="native-select" aria-label={t("Request type")} value={mode} onChange={event => change("mode", event.target.value)}>
        <option value="page">{t("Page: redirect to login")}</option>
        <option value="api">{t("API: return 401")}</option>
      </select>
    </Field>
    {mode === "api" && <Field label="API token target">
      <select className="native-select" aria-label={t("API token target")} value={target} onChange={event => change("apiTarget", event.target.value)}>
        <option value="">{t("No token forwarding")}</option>
        {target && !targets.includes(target) && <option value={target}>{target} ({t("unavailable")})</option>}
        {targets.map(id => <option key={id} value={id}>{id}</option>)}
      </select>
    </Field>}
  </div>;
}
