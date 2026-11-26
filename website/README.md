# Wafish website

Static marketing, documentation, and presentation source for <https://hallojoe.github.io/wafish/>.
Use Node 24 and npm. No backend runs on GitHub Pages.

```bash
cd website
npm ci
npx playwright install chromium
npm run check
npm test
npm run build
npm run test:browser
npm run package
node scripts/verify-offline.mjs
npm run preview
```

`npm run dev` serves a development build. Search needs a production build first.
`npm run preview` serves the built site below `/wafish/`.
The `Build` workflow builds the solution and website on pushes and pull requests.
Run `Website` manually for detailed checks, browser tests, and offline packaging.
Manual `Website` runs on `main` also deploy the website after successful checks.

## Content and identity

`src/lib/docs.ts` is the public document manifest. It maps canonical Markdown sources to public routes.
It does not publish the entire repository. Repository-only links resolve to GitHub.
New public guides belong in `docs/guide`. Keep configuration examples aligned with source contracts.
Mermaid loads only for documents containing a diagram. Pagefind indexes documentation after the Astro build.

`brand/mark.svg` is the root identity master. `npm run assets` creates exports, root PNG assets, and the administration favicon.
Keep the inline website and administration mark geometry aligned with that master.
Font files and licenses are self-hosted through the locked Fontsource packages.
The brand kit contains vector marks, outlined wordmarks, PNG variants, favicon, cover, and usage guidance.

## Screenshots

Screenshots use the real administration UI with isolated fixture data.
To refresh after an intentional interface change:

```bash
dotnet build ../tests/Wafish.ReactUi.Host -c Release
node scripts/capture.mjs
npm run build
```

The capture process owns its temporary host, password, browser, and database directory.
It never uses a production gateway. Source screenshots live in `brand/screenshots`.

## Homepage request film

The homepage follows four request stories through a shared 3D gateway.
Forwarding, WAF rejection, rate-limit rejection, and cache hits have distinct response paths.
`src/lib/request-journey.ts` defines the timeline and explanations.
The player and Three.js scene share one clock, so captions and geometry stay synchronized.

Playback advances through all four stories, then stops. Pause, replay, seek, and stage buttons support inspection.
Opening decision details pauses playback. Hidden pages and scenes stop advancing.
Reduced motion starts paused with a static diagram. WebGL failures retain manual navigation and explanations.

## Presentation

`src/lib/slides.ts` contains 22 timed slides and four optional appendix slides.
The main schedule totals 27 minutes. Speaker notes include demonstration instructions and support limits.
`src/lib/scenarios.mjs` defines six deterministic explanatory models.
One state controls each model's description, geometry, and event cue.
Scenes are illustrative. They do not call gateway APIs or claim measured performance.

Arrow keys navigate slides. S opens notes. F opens fullscreen.
Scene buttons step or replay a model. Backward navigation restores the scenario's starting state.
Sound starts disabled and requires a user action. Reduced motion uses static diagrams by default.
Hidden scenes stop rendering. WebGL failures retain diagrams and textual explanations.

`npm run package` exports a PDF handout, brand ZIP, and complete offline website.
The offline ZIP includes a dependency-free Node launcher. Run `node start.mjs` after extraction.
It needs a local HTTP server for modules and speaker notes. It needs no internet connection.
The PDF uses static diagrams and captions. It does not contain animated 3D or sound.

## Quality checks

Build checks reject missing local assets, broken page links, and broken internal anchors.
Unit checks verify scenario semantics, deterministic seeking, and talk timing.
Browser checks cover WebGL, static fallbacks, keyboard controls, notes, search, mobile layout, and accessibility.
Offline verification blocks external network requests and checks the packaged deck and documentation.
Review new visuals at desktop, mobile, and projector sizes before publishing.

## Archived release notes and evidence

The release-notes route reads one marked section from root `ARCHIVE.md`.
`src/lib/archive.ts` rejects missing, duplicate, reversed, and overlapping boundaries.
Keep section markers stable when editing archived release notes.
Links to other archived records resolve to GitHub. Current guides retain their public routes.

Historical website checks are preserved in [ARCHIVE.md](../ARCHIVE.md#archive-website-readme-md).
