import { documents, docUrl } from "../lib/docs";
import { url } from "../lib/site";
export function GET() {
  const paths = [
    url(),
    url("brand/"),
    url("presentation/"),
    ...documents.map((doc) => docUrl(doc.slug)),
  ];
  return new Response(
    `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${paths.map((path) => `<url><loc>https://hallojoe.github.io${path}</loc></url>`).join("")}</urlset>`,
    { headers: { "Content-Type": "application/xml" } },
  );
}
