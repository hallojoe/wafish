import test from "node:test";
import assert from "node:assert/strict";
import { scenarios, scenarioStep } from "../src/lib/scenarios.mjs";
import { slides } from "../src/lib/slides.ts";
test("blocked requests and cache hits never reach an upstream in the model", () => {
  assert.equal(scenarioStep("policy", 1).upstream, false);
  assert.equal(scenarioStep("cache", 1).upstream, false);
  assert.equal(scenarioStep("policy", 2).upstream, true);
});
test("retry and drain demonstrations avoid unavailable destinations", () => {
  for (const state of scenarios.resilience.steps.slice(1))
    assert.notEqual(state.destination, state.failed);
  for (const state of scenarios.lifecycle.steps) {
    assert.notEqual(state.destination, state.draining);
    assert.notEqual(state.destination, state.removed);
  }
});
test("a persisted change can exist before activation and after activation failure", () => {
  const committed = scenarioStep("configuration", 1);
  const failed = scenarioStep("configuration", 3);
  assert.equal(committed.persisted, 5);
  assert.equal(committed.active, 4);
  assert.equal(failed.persisted, 6);
  assert.equal(failed.active, 5);
});
test("seeking and replay are deterministic and reject unknown scenarios", () => {
  for (const [key, scenario] of Object.entries(scenarios)) {
    assert.deepEqual(
      scenarioStep(key, scenario.steps.length),
      scenarioStep(key, 0),
    );
    assert.deepEqual(scenarioStep(key, -1), scenario.steps.at(-1));
  }
  assert.throws(() => scenarioStep("unknown", 0));
});
test("the main talk has 22 slides, six models, notes, and a 27-minute schedule", () => {
  assert.equal(slides.length, 26);
  assert.equal(
    slides.slice(0, 22).reduce((sum, slide) => sum + slide.time, 0),
    1620,
  );
  assert.equal(
    new Set(slides.flatMap((slide) => (slide.scene ? [slide.scene] : []))).size,
    6,
  );
  for (const slide of slides) assert.ok(slide.notes.length > 200);
});
