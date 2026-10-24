# HyperHarbor wireframe machine

A low-poly PC, monitor, and backup disk as a glTF scene with named nodes, meant to be rendered edge-only
(Three.js EdgesGeometry plus LineSegments) as the navigation metaphor for the profile builder: each profile
section flies the camera to a part of the machine and lights it up.

## Files
Runtime assets live in client/static/machine (served by SvelteKit); this folder holds the source and the reference viewer.

- client/static/machine/hyperharbor-machine.glb: the model, 44 named nodes, about 1,400 triangles, 38 KB. Units are meters, +Y up, the
  monitor and the case front face +Z. The case sits at x 0.42, the monitor at x -0.12.
- client/static/machine/targets.json: camera target per profile section id: node to frame, look-at offset, distance, elevation, azimuth.
- client/static/machine/highlights.json: nodes to glow per section, plus per-control reactions (blink, pulse, spin, perItem).
- client/static/machine/exploded.svg: the client's still fallback (no WebGL, or under 30 frames per second for five
  seconds). The same 25 degree edges, parts pulled apart (EXPLODE in build_model.py), projected orthographically from
  the front right, one `<g data-node>` per node so the client applies the same highlights. The floor is left out.
- viewer.html: reference implementation (serve the repository root, for example `python -m http.server`, and open models/machine/viewer.html). Three.js from jsdelivr via an import map, EdgesGeometry conversion,
  the camera rig with a 900 ms eased fly and a slow tilted orbit at rest, bloom plus a CRT pass (scanlines,
  barrel distortion, vignette, flicker), amber and green palettes, reduced-motion handling. Query parameters:
  section, palette=green, still=1, model=<path>.
- build_model.py: regenerates the glb and both JSON files with trimesh. Edit geometry or targets here.
- shots/: still renders of several sections.

## Node names
Case, Case_Front, PowerButton, Board, CPU, CPU_Cooler, RAM_1..4, TPM_Chip, TPM_LED, GPU, GPU_Fan_1..2 (ring, hub, and
four blades, so a spin about the fan's axis shows),
DriveBay, Drive_1..4, PSU, RearIO, USB_1..4, Ethernet, Case_Fan, Monitor, Screen, Screen_Login,
Screen_Tab_1..5, Monitor_Neck, Monitor_Base, Backup_Cable_1..3, Backup_Disk, Backup_LED, Floor.

## Section to target map
overview Case, account Screen_Login, display Screen, browser Screen_Tab_3, packages DriveBay, debloat DriveBay,
firmware TPM_LED, compute CPU, gpu GPU, network Ethernet, backup Backup_Disk.

## In the client
The setup profile editor uses it (client/src/lib/machine and MachineView.svelte); docs/architecture.md lists the files.
Each part's edges are centered on the part with the part placed at that center, so a rotation turns it in place;
viewer.html does the same.

## Notes for using it
- Keep the glb and the two JSON files as static assets. Load with GLTFLoader, traverse meshes, replace each with
  `new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 25), material)` keyed by node name, as viewer.html does.
- Drive the camera from targets.json so adding a section is a JSON entry, not scene code.
- Per-control hooks: adding a package lights Drive_n (perItem), toggling TPM blinks TPM_LED, a backup schedule pulses
  Backup_LED, GPU settings spin GPU_Fan_1..2.
- Honor prefers-reduced-motion (viewer.html cuts instead of flying and disables the orbit) and fall back to a static
  image when WebGL is unavailable.
- Three.js r170 is what viewer.html was tested with. In the Tauri client, bundle three with npm instead of the
  import map.

## Regenerating
pip install trimesh numpy, then `python build_model.py` from this folder; it writes into client/static/machine (the glb,
both JSON files, and exploded.svg). A different trimesh version can write a glb with different bytes for the same
geometry; commit it only when the geometry changed. Node positions are plain numbers near the top of each
section in the script.
