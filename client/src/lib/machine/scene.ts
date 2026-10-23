// The Three.js scene of the machine view, after models/machine/viewer.html: every mesh drawn edge-only,
// the camera flown between section targets, highlighted parts over-bright under bloom, then a CRT pass.
// Loaded with a dynamic import so the client only pulls in three when the view can use WebGL.
import * as THREE from "three";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";
import { EffectComposer } from "three/addons/postprocessing/EffectComposer.js";
import { OutputPass } from "three/addons/postprocessing/OutputPass.js";
import { RenderPass } from "three/addons/postprocessing/RenderPass.js";
import { ShaderPass } from "three/addons/postprocessing/ShaderPass.js";
import { UnrealBloomPass } from "three/addons/postprocessing/UnrealBloomPass.js";
import { MODEL_URL, type MachineData, type MachinePalette } from "./assets";
import { CameraRig, cameraPosition, OVERVIEW, rigFor, targetFor, type Rig, type Vec3 } from "./camera";
import { FrameRateWatch } from "./fallback";
import {
  applyHighlight,
  NO_HIGHLIGHT,
  reactionFrame,
  resolveHighlight,
  type ControlState,
  type HighlightPart,
  type ResolvedHighlight,
} from "./highlights";

/** Faces meeting at less than this angle share no drawn edge, as in viewer.html. */
export const EDGE_THRESHOLD_DEG = 25;
/** Glow colors are scaled past 1.0 so the bloom threshold picks them up and the rest stays dark. */
const GLOW_GAIN = 2.2;
const FLOOR = "Floor";

const CRT_SHADER = {
  uniforms: {
    tDiffuse: { value: null },
    time: { value: 0 },
    flicker: { value: 1 },
    resolution: { value: new THREE.Vector2(1, 1) },
  },
  vertexShader: /* glsl */ `
    varying vec2 vUv;
    void main() {
      vUv = uv;
      gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }`,
  fragmentShader: /* glsl */ `
    uniform sampler2D tDiffuse;
    uniform float time;
    uniform float flicker;
    uniform vec2 resolution;
    varying vec2 vUv;
    void main() {
      vec2 c = vUv - 0.5;
      vec2 uv = 0.5 + c * (1.0 + 0.06 * dot(c, c));            // barrel distortion
      if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) { gl_FragColor = vec4(0.0, 0.0, 0.0, 1.0); return; }
      vec3 col = texture2D(tDiffuse, uv).rgb;
      float scan = 0.92 + 0.08 * sin(uv.y * resolution.y * 1.5708);   // one scanline per 4 device pixels
      float vignette = smoothstep(0.95, 0.35, length(uv - 0.5));
      float flick = mix(1.0, 0.985 + 0.015 * sin(time * 9.0), flicker);
      gl_FragColor = vec4(col * scan * (0.75 + 0.25 * vignette) * flick, 1.0);
    }`,
};

interface Part extends HighlightPart<THREE.LineBasicMaterial> {
  object: THREE.LineSegments<THREE.EdgesGeometry, THREE.LineBasicMaterial>;
  center: Vec3;
}

interface Materials {
  line: THREE.LineBasicMaterial;
  dim: THREE.LineBasicMaterial;
  glow: THREE.LineBasicMaterial;
  pulse: THREE.LineBasicMaterial;
}

export interface MachineSceneOptions {
  palette: MachinePalette;
  reducedMotion: boolean;
  /** Called once when the frame rate stayed too low; the scene stops rendering and the caller disposes it. */
  onslow: () => void;
}

function materialsFor(palette: MachinePalette): Materials {
  const glow = new THREE.LineBasicMaterial({ color: palette.glow });
  glow.color.multiplyScalar(GLOW_GAIN);
  const pulse = glow.clone();
  pulse.transparent = true;
  return {
    line: new THREE.LineBasicMaterial({ color: palette.line }),
    dim: new THREE.LineBasicMaterial({ color: palette.lineDim }),
    glow,
    pulse,
  };
}

/** Loads the model and starts rendering into the container. Throws when WebGL or the model is unavailable. */
export async function createMachineScene(container: HTMLElement, data: MachineData, options: MachineSceneOptions): Promise<MachineScene> {
  const gltf = await new GLTFLoader().loadAsync(MODEL_URL);
  return new MachineScene(container, gltf.scene, data, options);
}

export class MachineScene {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly composer: EffectComposer;
  private readonly crt: ShaderPass;
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(38, 1, 0.01, 20);
  private readonly parts = new Map<string, Part>();
  private readonly rig: CameraRig;
  private readonly watch = new FrameRateWatch();
  private readonly resizer: ResizeObserver;
  private readonly start = performance.now();
  private materials: Materials;
  private grid: THREE.GridHelper;
  private highlight: ResolvedHighlight = NO_HIGHLIGHT;
  private section = OVERVIEW;
  private controls: ControlState = {};
  private reducedMotion: boolean;
  private frameId = 0;
  private disposed = false;

  constructor(
    private readonly container: HTMLElement,
    model: THREE.Object3D,
    private readonly data: MachineData,
    private readonly options: MachineSceneOptions,
  ) {
    this.reducedMotion = options.reducedMotion;
    this.renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: "high-performance" });
    this.renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);

    this.materials = materialsFor(options.palette);
    this.scene.background = new THREE.Color(options.palette.background);
    this.grid = this.makeGrid(options.palette);

    // Every mesh becomes edge-only lines keyed by node name. The edges are centered on the part so a part
    // rotates about its own center (the fans spin in place).
    model.updateMatrixWorld(true);
    model.traverse((object) => {
      if (!(object instanceof THREE.Mesh)) return;
      const name = object.name || object.parent?.name || "unnamed";
      const edges = new THREE.EdgesGeometry(object.geometry, EDGE_THRESHOLD_DEG);
      edges.applyMatrix4(object.matrixWorld);
      edges.computeBoundingBox();
      const center = edges.boundingBox!.getCenter(new THREE.Vector3());
      edges.translate(-center.x, -center.y, -center.z);
      const base = name === FLOOR ? this.materials.dim : this.materials.line;
      const lines = new THREE.LineSegments(edges, base);
      lines.name = name;
      lines.position.copy(center);
      this.scene.add(lines);
      this.parts.set(name, { object: lines, base, center: [center.x, center.y, center.z] });
    });

    this.composer = new EffectComposer(this.renderer);
    this.composer.addPass(new RenderPass(this.scene, this.camera));
    this.composer.addPass(new UnrealBloomPass(new THREE.Vector2(1, 1), 0.7, 0.45, 0.25));
    this.crt = new ShaderPass(CRT_SHADER);
    this.composer.addPass(this.crt);
    this.composer.addPass(new OutputPass());

    const initial: Rig = targetFor(data.targets, OVERVIEW)
      ? this.rigFor(OVERVIEW)
      : { lookAt: [0, 0, 0], distance: 1.6, elevation: 22, azimuth: 35 };
    this.rig = new CameraRig(initial, this.reducedMotion, performance.now());

    this.resizer = new ResizeObserver(() => this.resize());
    this.resizer.observe(container);
    this.resize();
    document.addEventListener("visibilitychange", this.onVisibility);
    this.frameId = requestAnimationFrame(this.frame);
  }

  setSection(section: string): void {
    this.section = section;
    if (targetFor(this.data.targets, section)) this.rig.fly(this.rigFor(section), performance.now());
    this.refreshHighlight();
  }

  setControls(controls: ControlState): void {
    this.controls = { ...controls };
    this.refreshHighlight();
  }

  setPalette(palette: MachinePalette): void {
    const old = this.materials;
    this.materials = materialsFor(palette);
    for (const [name, part] of this.parts) part.base = name === FLOOR ? this.materials.dim : this.materials.line;
    this.scene.background = new THREE.Color(palette.background);
    this.scene.remove(this.grid);
    this.grid.dispose();
    this.grid = this.makeGrid(palette);
    this.refreshHighlight();
    for (const material of Object.values(old)) material.dispose();
  }

  setReducedMotion(reduced: boolean): void {
    this.reducedMotion = reduced;
    this.rig.setReducedMotion(reduced, performance.now());
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frameId);
    this.resizer.disconnect();
    document.removeEventListener("visibilitychange", this.onVisibility);
    for (const part of this.parts.values()) part.object.geometry.dispose();
    for (const material of Object.values(this.materials)) material.dispose();
    this.grid.dispose();
    this.composer.dispose();
    this.renderer.dispose();
    // The dialog can open and close many times; browsers cap live WebGL contexts.
    this.renderer.forceContextLoss();
    this.renderer.domElement.remove();
  }

  private rigFor(section: string): Rig {
    const target = targetFor(this.data.targets, section)!;
    return rigFor(target, (node) => this.parts.get(node)?.center);
  }

  private makeGrid(palette: MachinePalette): THREE.GridHelper {
    const grid = new THREE.GridHelper(1.6, 16, palette.lineDim, palette.lineDim);
    grid.position.set(0.15, 0.0005, -0.05);
    this.scene.add(grid);
    return grid;
  }

  private refreshHighlight(): void {
    for (const part of this.parts.values()) part.object.rotation.x = 0;
    this.highlight = resolveHighlight(this.data.highlights, this.section, this.controls);
    applyHighlight(this.parts, this.highlight, this.materials.glow, this.materials.pulse);
  }

  private resize(): void {
    const width = Math.max(1, this.container.clientWidth);
    const height = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(width, height, false);
    this.composer.setSize(width, height);
    this.crt.uniforms.resolution.value.set(width * this.renderer.getPixelRatio(), height * this.renderer.getPixelRatio());
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
  }

  private readonly onVisibility = () => this.watch.reset();

  private readonly frame = (now: number) => {
    if (this.disposed) return;
    if (this.watch.frame(now)) {
      this.options.onslow();
      return;
    }
    const rig = this.rig.update(now);
    this.camera.position.set(...cameraPosition(rig));
    this.camera.lookAt(...rig.lookAt);

    const seconds = (now - this.start) / 1000;
    const reaction = reactionFrame(seconds, this.reducedMotion);
    if (this.highlight.blink) {
      const part = this.parts.get(this.highlight.blink);
      if (part) part.object.visible = reaction.blinkVisible;
    }
    this.materials.pulse.opacity = reaction.pulseOpacity;
    for (const node of this.highlight.spin) {
      const part = this.parts.get(node);
      if (part) part.object.rotation.x = reaction.spinAngle;
    }

    this.crt.uniforms.time.value = seconds;
    this.crt.uniforms.flicker.value = this.reducedMotion ? 0 : 1;
    this.composer.render();
    this.frameId = requestAnimationFrame(this.frame);
  };
}
