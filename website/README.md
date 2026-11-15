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
The production workflow validates pull requests and deploys successful `main` builds.

## Content and identity

`src/lib/docs.ts` is the public document manifest. It maps canonical Markdown sources to public routes.
It does not publish the entire repository. Repository-only links resolve to GitHub.
New public guides belong in `docs/guide`. Keep configuration examples aligned with source contracts.
Mermaid loads only for documents containing a diagram. Pagefind indexes documentation after the Astro build.

`brand/mark.svg` is the root identity master. `npm run assets` creates exports and updates root PNG assets.
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

## Verification record

Verified locally on 2026-11-15:

- Static type checks: no errors or warnings.
- Five scenario checks and eight website browser checks passed.
- Links and assets passed across 29 HTML pages; search indexes 25 documents.
- The 26-slide PDF and offline edition passed export and network-isolation checks.
- Desktop, mobile, presentation, and PDF layouts received visual review.
- The onboarding sample forwarded a real backend response and passed host readiness.
- Administration build, lint, 81 unit tests, and 30 browser tests passed after logo integration.
- The packed React UI NuGet icon matches the new root PNG.

The existing administration coverage baseline remains below 80% overall.
This work changes its branding only; it does not claim increased application coverage.
