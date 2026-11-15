import * as THREE from "three";
import { scenarioStep } from "../lib/scenarios.mjs";

type State = ReturnType<typeof scenarioStep>;
export function createInstrument(
  host: HTMLElement,
  kind: string,
  initialPause: boolean,
) {
  const viewport = host.querySelector<HTMLElement>(".scene-viewport")!;
  const renderer = new THREE.WebGLRenderer({
    alpha: true,
    antialias: true,
    powerPreference: "low-power",
  });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  renderer.setClearColor(0x141915, 0);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.domElement.setAttribute("aria-hidden", "true");
  viewport.append(renderer.domElement);
  const world = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-9, 9, 5, -5, 0.1, 100);
  camera.position.set(10, 12, 14);
  camera.lookAt(0, 0, 0);
  world.add(new THREE.AmbientLight(0xd9e4cd, 2.6));
  const sun = new THREE.DirectionalLight(0xf5ffe5, 4);
  sun.position.set(-4, 10, 4);
  world.add(sun);
  const rim = new THREE.DirectionalLight(0xc9f46d, 2);
  rim.position.set(6, 3, -4);
  world.add(rim);
  const group = new THREE.Group();
  world.add(group);
  const material = (color: number, metalness = 0.35) =>
    new THREE.MeshStandardMaterial({ color, metalness, roughness: 0.42 });
  const graphite = material(0x303c2b);
  const pale = material(0xa7b794, 0.5);
  const lime = material(0xd5f36b);
  const orange = material(0xec956f);
  const dim = material(0x3d4935);

  function box(
    x: number,
    y: number,
    z: number,
    w: number,
    h: number,
    d: number,
    mat = graphite,
  ) {
    const geometry = new THREE.BoxGeometry(w, h, d);
    const mesh = new THREE.Mesh(geometry, mat);
    mesh.position.set(x, y, z);
    group.add(mesh);
    const lines = new THREE.LineSegments(
      new THREE.EdgesGeometry(geometry),
      new THREE.LineBasicMaterial({
        color: 0x758366,
        transparent: true,
        opacity: 0.5,
      }),
    );
    mesh.add(lines);
    return mesh;
  }
  function label(text: string, x: number, y: number, z: number, width = 3) {
    const canvas = document.createElement("canvas");
    canvas.width = 256;
    canvas.height = 64;
    const context = canvas.getContext("2d")!;
    context.font = (text.length > 18 ? "17" : "24") + "px monospace";
    context.fillStyle = "#e0e9d5";
    context.textAlign = "center";
    context.fillText(text, 128, 42);
    const texture = new THREE.CanvasTexture(canvas);
    const sprite = new THREE.Sprite(
      new THREE.SpriteMaterial({ map: texture, depthTest: false }),
    );
    sprite.position.set(x, y, z);
    sprite.scale.set(width, (width * 64) / 256, 1);
    group.add(sprite);
    return {
      sprite,
      set(text: string) {
        context.clearRect(0, 0, 256, 64);
        context.font = (text.length > 18 ? "17" : "24") + "px monospace";
        context.fillText(text, 128, 42);
        texture.needsUpdate = true;
      },
    };
  }
  function line(points: number[][], color: number, radius = 0.025) {
    const curve = new THREE.CatmullRomCurve3(
      points.map((p) => new THREE.Vector3(...(p as [number, number, number]))),
      false,
      "centripetal",
    );
    const mesh = new THREE.Mesh(
      new THREE.TubeGeometry(curve, 60, radius, 6, false),
      material(color),
    );
    group.add(mesh);
    return curve;
  }
  const grid = new THREE.GridHelper(28, 28, 0x435139, 0x2d3827);
  grid.position.y = -0.6;
  group.add(grid);
  const plate = box(0, -0.38, 0, 14.5, 0.22, 8.2);
  plate.material = material(0x20291c);
  // Three rails share the ingress and fan out into independent destinations.
  const rails = [-2.5, 0, 2.5].map((z) =>
    line(
      [
        [-6.7, -0.2, 0],
        [-3.8, -0.2, 0],
        [-1, -0.2, 0],
        [1.1, -0.2, 0],
        [3, -0.2, z],
        [6.2, -0.2, z],
      ],
      0x738d4a,
    ),
  );
  box(-3.8, 0.25, -1.12, 0.5, 1.15, 0.3, pale);
  box(-3.8, 0.25, 1.12, 0.5, 1.15, 0.3, pale);
  const gate = box(-3.8, 0.96, 0, 0.5, 0.28, 2.55, lime);
  const barrier = box(-3.8, 0.35, 0, 0.12, 0.9, 1.8, orange);
  barrier.visible = false;
  box(-0.3, 0.15, 0, 1.6, 0.7, 1.8, graphite);
  box(-0.3, 0.53, 0, 1.3, 0.08, 1.5, pale);
  label("YARP", -0.3, 1.1, 0, 2.4);
  label("POLICY", -3.8, 1.55, 0, 2.8);
  label("REQUEST", -6, 0.6, 0, 3);
  const targets = [-2.5, 0, 2.5].map((z, i) => {
    const base = box(5, 0.02, z, 2, 0.45, 1.5);
    const server = box(5, 0.6, z, 1.25, 0.85, 0.95, pale);
    const status = box(5, 1.06, z, 1.28, 0.08, 0.98, lime);
    const vents = Array.from({ length: 3 }, (_, j) =>
      box(4.36, 0.4 + j * 0.2, z, 0.025, 0.035, 0.65, graphite),
    );
    const title = label(
      `SERVICE ${String.fromCharCode(65 + i)}`,
      5,
      1.55,
      z,
      3,
    );
    return { base, server, status, title, vents };
  });
  const cache = box(-1.8, 0.2, 2.7, 1.4, 0.8, 1.25, lime);
  const cacheLabel = label("CACHE", -1.8, 1, 2.7, 3);
  const cacheRoute = line(
    [
      [-6.7, 0.02, 0],
      [-3.9, 0.02, 0],
      [-2.7, 0.02, 1],
      [-1.8, 0.02, 2.7],
      [-3.8, 0.02, 2.7],
      [-6.7, 0.02, 0],
    ],
    0xb9db66,
  );
  cache.visible = cacheLabel.sprite.visible = kind === "cache";
  // The cache rail is only visible in the cache model.
  const cacheRailMesh = group.children.at(-1)!;
  cacheRailMesh.visible = kind === "cache";
  const storedBank = box(-3.3, 0.2, -2.9, 2.5, 0.5, 1.1, pale);
  const activeBank = box(0.1, 0.2, -2.9, 2.5, 0.5, 1.1, lime);
  const persisted = label("PERSISTED / 4", -3.3, 1, -2.9, 3.4);
  const active = label("ACTIVE / 4", 0.1, 1, -2.9, 3.4);
  storedBank.visible =
    activeBank.visible =
    persisted.sprite.visible =
    active.sprite.visible =
      kind === "configuration";
  const particles = Array.from({ length: 7 }, (_, index) => {
    const particle = new THREE.Mesh(
      new THREE.SphereGeometry(index === 0 ? 0.13 : 0.075, 12, 8),
      lime,
    );
    group.add(particle);
    return particle;
  });
  let state: State = scenarioStep(kind, 0);
  let paused = initialPause;
  let inView = false;
  let frame = 0;
  let phase = 0.25;
  let lastTime = 0;
  const update = (next: State) => {
    state = next;
    phase = 0.25;
    barrier.visible = state.gate === "block";
    gate.material = state.gate === "pass" ? lime : orange;
    targets.forEach((target, index) => {
      target.server.material = state.failed === index ? dim : pale;
      target.status.material =
        state.failed === index || state.draining === index ? orange : lime;
      target.base.visible =
        target.server.visible =
        target.status.visible =
        target.title.sprite.visible =
          state.removed !== index;
      target.vents.forEach((vent) => {
        vent.visible = state.removed !== index;
      });
      target.title.set(
        state.draining === index
          ? "DRAINING"
          : state.failed === index
            ? "UNAVAILABLE"
            : `SERVICE ${String.fromCharCode(65 + index)}`,
      );
    });
    if (kind === "configuration") {
      persisted.set(`PERSISTED / ${state.persisted}`);
      active.set(`ACTIVE / ${state.active}`);
      storedBank.material = state.persisted !== state.active ? orange : pale;
    }
    if (kind === "cache") {
      cache.material = state.cache === "purge" ? dim : lime;
      cacheLabel.set(`CACHE / ${state.cache.toUpperCase()}`);
    }
    draw(0);
  };
  function draw(delta: number) {
    phase = (phase + delta * 0.00012) % 1;
    particles.forEach((particle, i) => {
      const p = (phase + i / particles.length) % 1;
      let position: THREE.Vector3;
      if (state.cache === "hit") position = cacheRoute.getPoint(p);
      else if (!state.upstream) position = rails[0].getPoint(p * 0.18);
      else position = rails[state.destination].getPoint(p);
      particle.position.copy(position);
      particle.position.y += 0.42;
      particle.material = state.gate === "block" ? orange : lime;
      particle.visible =
        state.cache !== "purge" &&
        (!state.probe || i === 0) &&
        (state.cache !== "shared" || i === 0 || p < 0.25);
    });
    renderer.render(world, camera);
  }
  function loop(time: number) {
    frame = 0;
    if (paused || !inView || document.hidden) return;
    draw(Math.min(time - lastTime, 40));
    lastTime = time;
    frame = requestAnimationFrame(loop);
  }
  function run() {
    if (!frame && !paused && inView && !document.hidden) {
      lastTime = performance.now();
      frame = requestAnimationFrame(loop);
    }
  }
  const resize = new ResizeObserver(() => {
    const width = viewport.clientWidth,
      height = viewport.clientHeight;
    if (!width || !height) return;
    renderer.setSize(width, height, false);
    const aspect = width / height;
    const extent = aspect < 1.7 ? 11 / aspect : 5.2;
    camera.left = -extent * aspect;
    camera.right = extent * aspect;
    camera.top = extent;
    camera.bottom = -extent;
    camera.updateProjectionMatrix();
    draw(0);
  });
  resize.observe(viewport);
  const visibility = new IntersectionObserver((entries) => {
    inView = entries[0].isIntersecting;
    run();
  });
  visibility.observe(viewport);
  document.addEventListener("visibilitychange", run);
  renderer.domElement.addEventListener("webglcontextlost", (event) => {
    event.preventDefault();
    paused = true;
    cancelAnimationFrame(frame);
    host.classList.remove("has-webgl");
    host.dataset.renderer = "static";
  });
  update(state);
  return {
    update,
    pause(value: boolean) {
      paused = value;
      run();
    },
  };
}
