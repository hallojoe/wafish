import { readFile } from "node:fs/promises";
import path from "node:path";
import { createMarkdownProcessor } from "@astrojs/markdown-remark";
import { load } from "cheerio";
import { url, github } from "./site";

export const documents = [
  ["", "Welcome", "Start here", "docs/guide/index.md"],
  [
    "getting-started",
    "Your first gateway",
    "Start here",
    "docs/guide/getting-started.md",
  ],
  [
    "architecture",
    "Architecture & hosting",
    "Concepts",
    "docs/architecture.md",
  ],
  ["plugins", "Plugin catalog", "Concepts", "docs/guide/plugins.md"],
  [
    "administration",
    "Administration workspace",
    "Configure",
    "docs/react-ui.md",
  ],
  [
    "configuration",
    "Configuration changes",
    "Configure",
    "docs/configuration-changes.md",
  ],
  [
    "mutations",
    "Mutation responses",
    "Configure",
    "docs/mutation-contracts.md",
  ],
  [
    "change-sets",
    "Change sets & policies",
    "Configure",
    "docs/configuration-policy-contracts.md",
  ],
  ["operations", "Operations guide", "Operate", "docs/guide/operations.md"],
  [
    "registration",
    "Registration & diagnostics",
    "Operate",
    "docs/registration-operations-contracts.md",
  ],
  ["security", "Security boundaries", "Operate", "docs/security.md"],
  ["aspire", "Local development with Aspire", "Operate", "docs/aspire.md"],
  ["https", "Sample HTTPS setup", "Operate", "docs/sample-https.md"],
  [
    "plugin-lifecycle",
    "Plugin lifecycle",
    "Extend",
    "docs/plugin-lifecycle-contract.md",
  ],
  ["plugins/waf", "WAF", "Plugin reference", "src/Wafish.Plugin.Waf/README.md"],
  [
    "plugins/rate-limit",
    "Local rate limits",
    "Plugin reference",
    "src/Wafish.Plugin.RateLimit/README.md",
  ],
  [
    "plugins/redis",
    "Redis rate limits",
    "Plugin reference",
    "src/Wafish.Plugin.RateLimit.Redis/README.md",
  ],
  [
    "plugins/cache",
    "Response cache",
    "Plugin reference",
    "src/Wafish.Plugin.Cache/README.md",
  ],
  [
    "plugins/retry",
    "Request retry",
    "Plugin reference",
    "src/Wafish.Plugin.Retry/README.md",
  ],
  [
    "plugins/circuit-breaker",
    "Circuit breaker",
    "Plugin reference",
    "src/Wafish.Plugin.CircuitBreaker/README.md",
  ],
  [
    "plugins/compression",
    "Compression",
    "Plugin reference",
    "src/Wafish.Plugin.Compression/README.md",
  ],
  [
    "plugins/metrics",
    "Route & cluster metrics",
    "Plugin reference",
    "src/Wafish.Plugin.Metrics/README.md",
  ],
  [
    "plugins/logs",
    "Request logs",
    "Plugin reference",
    "src/Wafish.Plugin.ProxyLog/README.md",
  ],
  [
    "plugins/discovery",
    "Service discovery",
    "Plugin reference",
    "src/Wafish.Plugin.ServiceDiscovery/README.md",
  ],
  [
    "release-notes",
    "1.0.0-rc.1 release notes",
    "Release",
    "docs/release-notes-1.0.0-rc.1.md",
  ],
].map(([slug, title, group, source]) => ({ slug, title, group, source }));
export const docUrl = (slug: string) => url(`docs/${slug ? slug + "/" : ""}`);
// Astro bundles server modules into a temporary directory; cwd stays the website project.
const root = path.resolve("..");
const processor = createMarkdownProcessor({
  shikiConfig: { theme: "github-dark" },
});
export async function renderDocument(document: (typeof documents)[number]) {
  let markdown = await readFile(path.join(root, document.source), "utf8");
  markdown = markdown
    .replace(/^# .+\r?\n/, "")
    .replace(/^\[README\].+\r?\n/m, "");
  markdown = markdown.replace(
    /```mermaid\r?\n([\s\S]*?)```/g,
    (_, code) =>
      `<pre data-mermaid>${code.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")}</pre>`,
  );
  const result = await (await processor).render(markdown);
  const $ = load(result.code, {}, false);
  $("a[href], img[src]").each((_, element) => {
    const attr = element.tagName === "img" ? "src" : "href";
    const value = $(element).attr(attr)!;
    if (/^(?:[a-z]+:|\/|#)/i.test(value)) return;
    const [relative, fragment] = value.split("#");
    const source = path.posix.normalize(
      path.posix.join(
        path.posix.dirname(document.source),
        decodeURIComponent(relative),
      ),
    );
    const entry = documents.find((item) => item.source === source);
    const target = entry
      ? docUrl(entry.slug)
      : attr === "src" && source.startsWith("docs/")
        ? url(`images/docs/${path.posix.basename(source)}`)
        : `${github}/blob/main/${source}`;
    $(element).attr(attr, target + (fragment ? "#" + fragment : ""));
  });
  const headings = $("h2[id]")
    .map((_, element) => ({
      id: $(element).attr("id")!,
      text: $(element).text(),
    }))
    .get();
  return { html: $.html(), headings };
}
