import {
  journeyCases,
  journeyStages,
  journeyDetail,
  journeyOrder,
  journeyPath,
  journeyDuration,
  journeyFrame,
  shotDuration,
  type JourneyCase,
} from "../lib/request-journey";
import { url } from "../lib/site";
import type { createRequestMovie } from "./request-movie-3d";

export function mountJourney(host: HTMLElement) {
  if (host.dataset.mounted) return;
  host.dataset.mounted = "true";
  const get = <T extends HTMLElement>(selector: string) =>
    host.querySelector<T>(selector)!;
  const viewport = get(".journey-viewport");
  const play = get<HTMLButtonElement>("[data-journey-play]");
  const seek = get<HTMLInputElement>("[data-journey-seek]");
  const motion = matchMedia("(prefers-reduced-motion: reduce)");
  let kind: JourneyCase = "forwarded";
  let elapsed = 0;
  let playing = !motion.matches;
  let inView = false;
  let frame = 0;
  let lastTime = 0;
  let lastShot = "";
  let loading = false;
  let unavailable = false;
  let disposed = false;
  let movie: ReturnType<typeof createRequestMovie> | undefined;
  const clock = (ms: number) =>
    `00:${String(Math.floor(ms / 1000)).padStart(2, "0")}`;
  const active = () => playing && inView && !document.hidden && !unavailable;

  function render() {
    const { stage, progress } = journeyFrame(kind, elapsed);
    const example = journeyCases[kind];
    const shotKey = `${kind}:${stage}`;
    if (shotKey !== lastShot) {
      lastShot = shotKey;
      const detail = journeyDetail(kind, stage);
      host.dataset.case = kind;
      host.dataset.stage = String(stage);
      get("[data-journey-chapter]").textContent =
        `0${journeyOrder.indexOf(kind) + 1} / ${example.label.toUpperCase()}`;
      get("[data-journey-counter]").textContent =
        `0${stage + 1} / ${journeyStages[stage].name.toUpperCase()}`;
      get("[data-journey-title]").textContent = detail.title;
      get("[data-journey-decision]").textContent = detail.decision;
      get("[data-journey-text]").textContent = detail.text;
      get("[data-journey-next]").textContent = detail.next;
      get<HTMLAnchorElement>("[data-journey-doc]").href = url(
        `docs/${journeyStages[stage].doc}`,
      );
      get("[data-journey-progress]").textContent =
        stage === 5
          ? `${example.status} · ${kind === "forwarded" ? "1 upstream request" : "0 upstream requests"} · ${example.source}`
          : stage === example.stop
            ? kind === "forwarded"
              ? "1 upstream request · Catalog 01"
              : `0 upstream requests · ${example.source}`
            : "No upstream request yet";
      get("[data-journey-direction]").textContent =
        stage === 5 ? "RESPONSE TO CLIENT" : "REQUEST";
      viewport.setAttribute(
        "aria-label",
        `${example.label}. ${detail.title} ${detail.decision}`,
      );
      get("[data-journey-advance]").textContent =
        stage === 5
          ? kind === journeyOrder.at(-1)
            ? "Replay all →"
            : "Next story →"
          : "Next stage →";
      host
        .querySelectorAll<HTMLButtonElement>("[data-journey-case]")
        .forEach((button) =>
          button.setAttribute(
            "aria-pressed",
            String(button.dataset.journeyCase === kind),
          ),
        );
      host
        .querySelectorAll<HTMLElement>("[data-journey-node]")
        .forEach((node, index) => {
          const skipped = index > example.stop && index < 5;
          const state = skipped
            ? "skipped"
            : index === stage
              ? "current"
              : index < stage
                ? "complete"
                : "pending";
          node.dataset.state = state;
          const button = node.querySelector<HTMLButtonElement>("button")!;
          button.disabled = skipped;
          if (index === stage) button.setAttribute("aria-current", "step");
          else button.removeAttribute("aria-current");
          node.querySelector<HTMLElement>("[data-journey-state]")!.textContent =
            {
              skipped: "Skipped",
              current: "Current",
              complete: "Complete",
              pending: "Upcoming",
            }[state];
        });
      // The fallback uses the same decisions as the film, including early returns.
      const destination = stage === 5 ? 0 : stage === 4 ? 6 : stage + 1;
      const packet = get<SVGElement & HTMLElement>("[data-journey-packet]");
      packet.setAttribute("cx", String(80 + destination * 140));
      packet.setAttribute(
        "fill",
        (kind === "blocked" || kind === "limited") && stage >= example.stop
          ? "#ec956f"
          : stage === 5
            ? "#a6cbe3"
            : "#d5f36b",
      );
      get("[data-journey-return]").setAttribute(
        "d",
        `M${80 + (example.stop === 4 ? 6 : example.stop + 1) * 140} 160V280H80V160`,
      );
      get("[data-journey-return]").setAttribute(
        "opacity",
        stage === 5 ? "1" : ".15",
      );
      host
        .querySelectorAll<SVGElement>("[data-journey-fallback-node]")
        .forEach((node, index) => {
          node.setAttribute(
            "opacity",
            index > example.stop + 1 && index > 0 && kind !== "forwarded"
              ? ".3"
              : "1",
          );
        });
      seek.max = String(journeyDuration(kind));
      seek.setAttribute(
        "aria-valuetext",
        `${example.label}: ${journeyStages[stage].name}`,
      );
    }
    seek.value = String(elapsed);
    const time = `${clock(elapsed)} / ${clock(journeyDuration(kind))}`;
    const display = get("[data-journey-clock]");
    if (display.textContent !== time) display.textContent = time;
    movie?.draw(kind, stage, progress, motion.matches);
  }
  function syncPlayback() {
    cancelAnimationFrame(frame);
    frame = 0;
    host.dataset.playing = String(active());
    get("[data-journey-play-icon]").textContent = unavailable
      ? ""
      : playing
        ? "⏸"
        : "▶";
    get("[data-journey-play-label]").textContent = unavailable
      ? "Static view"
      : playing
        ? "Pause"
        : "Play";
    play.setAttribute(
      "aria-label",
      unavailable
        ? "Static view: 3D unavailable"
        : playing
          ? "Pause stories"
          : "Play stories",
    );
    play.disabled = unavailable;
    get(".journey-caption").setAttribute(
      "aria-live",
      playing ? "off" : "polite",
    );
    if (active() && movie) {
      lastTime = performance.now();
      frame = requestAnimationFrame(tick);
    }
  }
  function tick(now: number) {
    frame = 0;
    if (!active()) return;
    elapsed += Math.min(now - lastTime, 100);
    lastTime = now;
    if (elapsed >= journeyDuration(kind)) {
      const next = journeyOrder.indexOf(kind) + 1;
      if (next < journeyOrder.length) {
        kind = journeyOrder[next];
        elapsed = 0;
      } else {
        elapsed = journeyDuration(kind);
        playing = false;
        syncPlayback();
      }
    }
    render();
    if (active()) frame = requestAnimationFrame(tick);
  }
  function fallback() {
    unavailable = true;
    playing = false;
    host.dataset.renderer = "static";
    movie?.dispose();
    movie = undefined;
    syncPlayback();
    render();
  }
  async function loadMovie() {
    if (movie || loading || unavailable || disposed) return;
    loading = true;
    try {
      const { createRequestMovie } = await import("./request-movie-3d");
      if (disposed) return;
      movie = createRequestMovie(viewport, fallback);
      host.dataset.renderer = "webgl";
      render();
      syncPlayback();
    } catch {
      fallback();
    } finally {
      loading = false;
    }
  }
  function setStage(stage: number) {
    playing = false;
    const path = journeyPath(kind);
    elapsed =
      path
        .slice(0, path.indexOf(stage))
        .reduce((total, item) => total + shotDuration(item), 0) +
      shotDuration(stage) * 0.7;
    syncPlayback();
    render();
  }
  play.addEventListener("click", () => {
    if (elapsed >= journeyDuration(kind)) {
      elapsed = 0;
    }
    playing = !playing;
    if (playing) void loadMovie();
    syncPlayback();
    render();
  });
  get("[data-journey-replay]").addEventListener("click", () => {
    elapsed = 0;
    playing = !motion.matches && !unavailable;
    syncPlayback();
    render();
  });
  get("[data-journey-advance]").addEventListener("click", () => {
    const { stage } = journeyFrame(kind, elapsed);
    if (stage === 5) {
      kind =
        journeyOrder[(journeyOrder.indexOf(kind) + 1) % journeyOrder.length];
      elapsed = 0;
      playing = false;
      syncPlayback();
      render();
    } else setStage(journeyPath(kind)[journeyPath(kind).indexOf(stage) + 1]);
  });
  seek.addEventListener("input", () => {
    playing = false;
    elapsed = Number(seek.value);
    syncPlayback();
    render();
  });
  host
    .querySelectorAll<HTMLButtonElement>("[data-journey-case]")
    .forEach((button) =>
      button.addEventListener("click", () => {
        kind = button.dataset.journeyCase as JourneyCase;
        elapsed = 0;
        // Selecting a chapter starts at its beginning and preserves the playback choice.
        syncPlayback();
        render();
      }),
    );
  host
    .querySelectorAll<HTMLButtonElement>("[data-journey-stage]")
    .forEach((button) =>
      button.addEventListener("click", () =>
        setStage(Number(button.dataset.journeyStage)),
      ),
    );
  get<HTMLDetailsElement>(".journey-details").addEventListener(
    "toggle",
    (event) => {
      if ((event.currentTarget as HTMLDetailsElement).open) {
        playing = false;
        syncPlayback();
      }
    },
  );
  const observer = new IntersectionObserver(
    (entries) => {
      inView = entries[0].isIntersecting;
      if (inView && !motion.matches) void loadMovie();
      syncPlayback();
    },
    { threshold: 0.15 },
  );
  observer.observe(viewport);
  document.addEventListener("visibilitychange", syncPlayback);
  motion.addEventListener("change", () => {
    if (motion.matches) playing = false;
    syncPlayback();
    render();
  });
  window.addEventListener("pagehide", (event) => {
    cancelAnimationFrame(frame);
    if (!event.persisted) {
      disposed = true;
      observer.disconnect();
      movie?.dispose();
      document.removeEventListener("visibilitychange", syncPlayback);
    }
  });
  window.addEventListener("pageshow", syncPlayback);
  get(".journey-options").hidden = false;
  get(".journey-player").hidden = false;
  host.dataset.renderer = "static";
  syncPlayback();
  render();
}
