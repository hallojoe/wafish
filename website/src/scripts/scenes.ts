import { scenarioStep, scenarios } from "../lib/scenarios.mjs";

export function mountScenes() {
  document
    .querySelectorAll<HTMLElement>("[data-scene]:not([data-mounted])")
    .forEach((host) => {
      host.dataset.mounted = "true";
      const kind = host.dataset.scene as keyof typeof scenarios;
      let step = 0;
      let paused = matchMedia("(prefers-reduced-motion: reduce)").matches;
      let update:
        ((state: ReturnType<typeof scenarioStep>) => void) | undefined;
      let pause: ((value: boolean) => void) | undefined;
      let started = false;
      const caption = host.querySelector<HTMLElement>(".scene-caption")!;
      const motion = host.querySelector<HTMLButtonElement>(
        "[data-scene-motion]",
      );
      const publish = (index: number, sound = true) => {
        step =
          ((index % scenarios[kind].steps.length) +
            scenarios[kind].steps.length) %
          scenarios[kind].steps.length;
        const state = scenarioStep(kind, step);
        host.dataset.step = String(step);
        host.dataset.outcome = state.gate;
        caption.textContent = state.text;
        host
          .querySelector("[data-block-bar]")
          ?.setAttribute(
            "visibility",
            state.gate === "block" ? "visible" : "hidden",
          );
        host
          .querySelector("[data-upstream]")
          ?.setAttribute("opacity", state.upstream ? "1" : ".15");
        const stored = host.querySelector("[data-persisted]");
        const active = host.querySelector("[data-active]");
        if (stored) stored.textContent = `PERSISTED / ${state.persisted}`;
        if (active) active.textContent = `ACTIVE / ${state.active}`;
        const cache = host.querySelector("[data-cache-state]");
        if (cache) cache.textContent = `CACHE / ${state.cache.toUpperCase()}`;
        host
          .querySelector("[data-cache-return]")
          ?.setAttribute("opacity", state.cache === "hit" ? "1" : ".3");
        host.querySelectorAll("[data-destination]").forEach((element) => {
          const index = Number(element.getAttribute("data-destination"));
          element.setAttribute(
            "fill",
            state.failed === index || state.draining === index
              ? "#5a3424"
              : "#263022",
          );
          element.setAttribute("opacity", state.removed === index ? ".2" : "1");
          const label = host.querySelector(
            `[data-destination-label="${index}"]`,
          );
          if (label)
            label.textContent =
              state.removed === index
                ? "REMOVED"
                : state.draining === index
                  ? "DRAINING"
                  : state.failed === index
                    ? "FAILED"
                    : `SERVICE ${String.fromCharCode(65 + index)}`;
        });
        update?.(state);
        if (sound)
          host.dispatchEvent(
            new CustomEvent("wafish:scene", {
              bubbles: true,
              detail: { kind, step, state },
            }),
          );
      };
      host
        .querySelector("[data-scene-next]")
        ?.addEventListener("click", () => publish(step + 1));
      host
        .querySelector("[data-scene-reset]")
        ?.addEventListener("click", () => publish(0));
      host.addEventListener("scene:set", (event) =>
        publish((event as CustomEvent).detail, false),
      );
      function setPause(value: boolean) {
        paused = value;
        pause?.(value);
        if (motion) {
          motion.textContent = paused ? "Play motion" : "Pause motion";
          motion.setAttribute("aria-pressed", String(paused));
        }
      }
      setPause(paused);
      publish(0, false);
      motion?.addEventListener("click", () => {
        setPause(!paused);
        if (!started && !paused) void start();
      });
      async function start() {
        if (started) return;
        started = true;
        try {
          const { createInstrument } = await import("./instrument");
          const instrument = createInstrument(host, kind, paused);
          update = instrument.update;
          pause = instrument.pause;
          update(scenarioStep(kind, step));
          host.classList.add("has-webgl");
          host.dataset.renderer = "webgl";
        } catch {
          // Text and SVG remain usable when WebGL is disabled or its context cannot be created.
          host.dataset.renderer = "static";
          if (motion) motion.hidden = true;
        }
      }
      const observer = new IntersectionObserver((entries) => {
        if (entries.some((entry) => entry.isIntersecting)) {
          if (!paused) void start();
          pause?.(paused);
        } else pause?.(true);
      });
      observer.observe(host);
    });
}
