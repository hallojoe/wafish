export function GET() {
  return new Response(
    "User-agent: *\nAllow: /wafish/\nSitemap: https://hallojoe.github.io/wafish/sitemap.xml\n",
  );
}
