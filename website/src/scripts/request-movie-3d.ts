import * as THREE from "three";
import { journeyCases, type JourneyCase } from "../lib/request-journey";

// The film shares its stage and clock with the captions and player controls.
export function createRequestMovie(
  viewport: HTMLElement,
  onContextLost: () => void,
) {
  const renderer = new THREE.WebGLRenderer({
    alpha: true,
    antialias: true,
    powerPreference: "low-power",
  });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.domElement.setAttribute("aria-hidden", "true");
  viewport.append(renderer.domElement);
  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-12, 12, 6, -6, 0.1, 100);
  scene.add(new THREE.AmbientLight(0xe3ecd9, 2.2));
  const sun = new THREE.DirectionalLight(0xf3ffe0, 3.5);
  sun.position.set(-5, 12, 6);
  scene.add(sun);
  const rim = new THREE.DirectionalLight(0x9dd1e7, 2);
  rim.position.set(7, 6, -8);
  scene.add(rim);
  const material = (color: number, emissive = 0x000000) =>
    new THREE.MeshStandardMaterial({
      color,
      emissive,
      emissiveIntensity: 0.4,
      metalness: 0.3,
      roughness: 0.45,
    });
  const dark = material(0x263122),
    pale = material(0x829178),
    lime = material(0xd5f36b, 0x718b18);
  const dim = material(0x30392d),
    orange = material(0xec956f, 0x8b3518),
    blue = material(0xa6cbe3, 0x325879);
  const root = new THREE.Group();
  scene.add(root);
  function box(
    x: number,
    y: number,
    z: number,
    w: number,
    h: number,
    d: number,
    mat = dark,
  ) {
    const geometry = new THREE.BoxGeometry(w, h, d);
    const mesh = new THREE.Mesh(geometry, mat);
    mesh.position.set(x, y, z);
    const edges = new THREE.LineSegments(
      new THREE.EdgesGeometry(geometry),
      new THREE.LineBasicMaterial({
        color: 0x789367,
        transparent: true,
        opacity: 0.4,
      }),
    );
    mesh.add(edges);
    root.add(mesh);
    return mesh;
  }
  function label(
    text: string,
    x: number,
    y: number,
    z: number,
    width = 2.5,
    color = "#dce5d2",
  ) {
    const canvas = document.createElement("canvas");
    canvas.width = 512;
    canvas.height = 96;
    const context = canvas.getContext("2d")!;
    context.font = "64px monospace";
    context.textAlign = "center";
    context.fillStyle = color;
    context.fillText(text, 256, 72);
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    const sprite = new THREE.Sprite(
      new THREE.SpriteMaterial({
        map: texture,
        depthTest: false,
        transparent: true,
      }),
    );
    sprite.position.set(x, y, z);
    sprite.scale.set(width, (width * 96) / 512, 1);
    root.add(sprite);
    return sprite;
  }
  const v = (x: number, z = 0) => new THREE.Vector3(x, 0.72, z);
  function rail(points: THREE.Vector3[], mat = pale, radius = 0.025) {
    const curve = new THREE.CatmullRomCurve3(points, false, "centripetal");
    const mesh = new THREE.Mesh(
      new THREE.TubeGeometry(curve, 60, radius, 6, false),
      mat,
    );
    root.add(mesh);
    return { curve, mesh };
  }
  const grid = new THREE.GridHelper(50, 50, 0x435238, 0x2c3825);
  grid.material.transparent = true;
  grid.material.opacity = 0.3;
  grid.position.y = -0.62;
  root.add(grid);
  box(-0.2, -0.42, 0.6, 19.8, 0.28, 7.3);
  // Route, WAF, allowance, cache, and proxy are distinct physical stations.
  const xs = [-5.8, -3.2, -0.6, 2, 4.6];
  const stations = xs.map((x, index) => {
    box(x, -0.08, 0, 1.85, 0.4, 2.5);
    const strip = box(x, 0.15, 1.16, 1.85, 0.06, 0.12, pale);
    label(
      ["ROUTE", "WAF", "RATE LIMIT", "CACHE", "YARP"][index],
      x,
      2.9,
      0,
      index === 2 ? 2.6 : 2.1,
    );
    if (index === 1 || index === 2) {
      box(x, 0.86, -1, 0.35, 1.6, 0.3, pale);
      box(x, 0.86, 1, 0.35, 1.6, 0.3, pale);
      box(x, 1.65, 0, 0.4, 0.22, 2.3, pale);
    } else if (index === 3) {
      for (let layer = 0; layer < 3; layer++) {
        const disk = new THREE.Mesh(
          new THREE.CylinderGeometry(0.65, 0.65, 0.28, 32),
          layer === 2 ? pale : dark,
        );
        disk.position.set(x, 0.38 + layer * 0.36, -0.45);
        root.add(disk);
      }
    } else {
      box(x, 0.46, -0.3, 1.15, 0.65, 1.1);
      box(x, 0.83, -0.3, 1.15, 0.07, 1.1, pale);
    }
    return strip;
  });
  box(-8.5, 0.45, 0, 1, 1.1, 1.4, pale);
  label("CLIENT", -8.5, 2.2, 0, 2.2);
  const serviceA = box(8, 0.85, -1.3, 1.15, 1.9, 1.25, pale);
  box(8, -0.08, -1.3, 2, 0.4, 2);
  box(8, 0.85, 1.5, 1.15, 1.9, 1.25, dark);
  box(8, -0.08, 1.5, 2, 0.4, 2);
  for (const z of [-1.3, 1.5]) {
    for (let row = 0; row < 4; row++)
      box(7.4, 0.3 + row * 0.35, z, 0.025, 0.05, 0.85, lime);
  }
  label("CATALOG 01", 8, 2.9, -1.3, 2.8);
  label("CATALOG 02", 8, 2.9, 1.5, 2.8);
  const positions = [v(-8.5), ...xs.map((x) => v(x))];
  const routes = xs.map((_, i) => rail([positions[i], positions[i + 1]]));
  const forward = rail([v(2), v(4.6), v(6.1, -0.7), v(8, -1.3)]);
  rail([v(4.6), v(6.1, 0.8), v(8, 1.5)], dim);
  const returnPaths = Object.fromEntries(
    Object.entries(journeyCases).map(([key, example]) => {
      const origin = example.stop === 4 ? v(8, -1.3) : v(xs[example.stop]);
      const path = rail(
        [
          origin,
          v(origin.x + 0.6, 2.7),
          v(origin.x, 3.2),
          v(-7.6, 3.2),
          v(-8.5, 0),
        ],
        blue,
        0.035,
      );
      path.mesh.visible = false;
      return [key, path];
    }),
  ) as Record<JourneyCase, ReturnType<typeof rail>>;
  const barrier = box(-3.2, 0.82, 0, 0.15, 1.25, 1.7, orange);
  barrier.visible = false;
  const packet = new THREE.Mesh(new THREE.IcosahedronGeometry(0.22, 1), lime);
  root.add(packet);
  const halo = new THREE.Mesh(
    new THREE.SphereGeometry(0.4, 20, 12),
    new THREE.MeshBasicMaterial({
      color: 0xd5f36b,
      transparent: true,
      opacity: 0.13,
      depthWrite: false,
    }),
  );
  root.add(halo);
  const tail = Array.from({ length: 9 }, (_, i) => {
    const dot = new THREE.Mesh(
      new THREE.SphereGeometry(0.065 * (1 - i / 12), 8, 6),
      lime,
    );
    root.add(dot);
    return dot;
  });
  const packetLight = new THREE.PointLight(0xd5f36b, 6, 3);
  root.add(packetLight);
  const focusRing = new THREE.Mesh(
    new THREE.TorusGeometry(1.1, 0.023, 8, 64),
    lime,
  );
  focusRing.rotation.x = Math.PI / 2;
  root.add(focusRing);
  let aspect = 2;
  let last: [JourneyCase, number, number, boolean] = ["forwarded", 0, 0, false];
  const smooth = (n: number) => n * n * (3 - 2 * n);
  function draw(
    kind: JourneyCase,
    stage: number,
    progress: number,
    reduced: boolean,
  ) {
    last = [kind, stage, progress, reduced];
    const example = journeyCases[kind];
    const rejection = kind === "blocked" || kind === "limited";
    const returning = stage === 5;
    const reached = stage >= example.stop;
    const tint = returning
      ? rejection
        ? orange
        : blue
      : rejection && reached
        ? orange
        : lime;
    const path = returning
      ? returnPaths[kind].curve
      : stage === 4
        ? forward.curve
        : routes[stage].curve;
    // Travel, then hold at the station while the caption explains the decision.
    const travel = smooth(Math.min(progress / 0.62, 1));
    packet.position.copy(path.getPoint(travel));
    packet.material = tint;
    packet.rotation.set(progress * 2, progress * 3, 0.3);
    halo.position.copy(packet.position);
    halo.material.color.copy(tint.color);
    halo.scale.setScalar(1 + 0.12 * Math.sin(progress * Math.PI * 6));
    packetLight.position.copy(packet.position);
    packetLight.color.copy(tint.color);
    tail.forEach((dot, i) => {
      dot.material = tint;
      dot.visible = progress > 0.02 && progress < 0.7;
      dot.position.copy(path.getPoint(Math.max(0, travel - (i + 1) * 0.025)));
    });
    stations.forEach((strip, i) => {
      strip.material =
        i === stage || (returning && i === example.stop)
          ? tint
          : i < stage && i <= example.stop
            ? lime
            : dim;
    });
    routes.forEach((route, i) => {
      route.mesh.material = i <= Math.min(stage, example.stop) ? pale : dim;
    });
    forward.mesh.material = stage >= 4 && kind === "forwarded" ? lime : dim;
    serviceA.material = stage >= 4 && kind === "forwarded" ? pale : dark;
    Object.entries(returnPaths).forEach(([key, route]) => {
      route.mesh.visible = returning && key === kind;
      route.mesh.material = tint;
    });
    barrier.visible = rejection && reached;
    barrier.position.x = xs[example.stop];
    focusRing.visible = !returning;
    focusRing.position.set(
      stage === 4 ? 8 : xs[returning ? example.stop : stage],
      0.2,
      stage === 4 ? -1.3 : 0,
    );
    focusRing.material = tint;
    const focusX = returning
      ? packet.position.x
      : THREE.MathUtils.lerp(
          positions[stage].x,
          stage === 4 ? 7 : xs[stage],
          smooth(Math.min(progress / 0.7, 1)),
        );
    const mobile = aspect < 1.35;
    const centerX = reduced
      ? mobile
        ? returning
          ? xs[example.stop]
          : xs[stage]
        : 0
      : focusX * (mobile ? 0.85 : 0.22);
    const target = new THREE.Vector3(centerX, 0.2, 0.45);
    camera.position.set(
      centerX + (mobile ? 4 : 6),
      mobile ? 9 : 12,
      mobile ? 12 : 19,
    );
    camera.lookAt(target);
    const extent = mobile ? 5.6 / aspect : 5.5;
    camera.left = -extent * aspect;
    camera.right = extent * aspect;
    camera.top = extent;
    camera.bottom = -extent;
    camera.updateProjectionMatrix();
    renderer.render(scene, camera);
  }
  const resize = new ResizeObserver(() => {
    const width = viewport.clientWidth,
      height = viewport.clientHeight;
    if (!width || !height) return;
    aspect = width / height;
    renderer.setSize(width, height, false);
    draw(...last);
  });
  resize.observe(viewport);
  renderer.domElement.addEventListener("webglcontextlost", (event) => {
    event.preventDefault();
    onContextLost();
  });
  draw(...last);
  return {
    draw,
    dispose() {
      resize.disconnect();
      const geometries = new Set<THREE.BufferGeometry>();
      const materials = new Set<THREE.Material>();
      root.traverse((object) => {
        if (
          object instanceof THREE.Mesh ||
          object instanceof THREE.LineSegments ||
          object instanceof THREE.Sprite
        ) {
          if ("geometry" in object) geometries.add(object.geometry);
          (Array.isArray(object.material)
            ? object.material
            : [object.material]
          ).forEach((mat) => materials.add(mat));
        }
      });
      geometries.forEach((geometry) => geometry.dispose());
      materials.forEach((mat) => {
        if ("map" in mat && mat.map instanceof THREE.Texture) mat.map.dispose();
        mat.dispose();
      });
      renderer.dispose();
      renderer.domElement.remove();
    },
  };
}
