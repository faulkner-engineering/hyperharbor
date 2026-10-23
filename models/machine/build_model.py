"""Build the HyperHarbor wireframe machine as a glTF scene with named nodes.

Every part is a clean low-poly mesh so Three.js EdgesGeometry can render it edge-only.
Node names are the camera and highlight targets used by the profile builder.
Units are meters. The scene faces +Z (front of the case and the monitor face the viewer).
"""
import json
import os

import numpy as np
import trimesh
from trimesh.creation import box, cylinder, annulus

OUT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "client", "static", "machine"))
os.makedirs(OUT, exist_ok=True)


def at(mesh, x=0.0, y=0.0, z=0.0, rot=None):
    """Translate (and optionally rotate) a mesh in place and return it."""
    if rot is not None:
        axis, angle = rot
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.radians(angle), axis))
    mesh.apply_translation([x, y, z])
    return mesh


def frame_box(w, h, d, t):
    """A rectangular frame (box with a rectangular hole), extruded along Z."""
    outer = box(extents=[w, h, d])
    inner = box(extents=[w - 2 * t, h - 2 * t, d * 1.2])
    return trimesh.boolean.difference([outer, inner]) if _boolean_ok() else outer


def _boolean_ok():
    try:
        trimesh.boolean.difference([box(), box(extents=[0.5, 0.5, 2])])
        return True
    except BaseException:
        return False


scene = trimesh.Scene()

# ---------------------------------------------------------------------------
# Case: a mid tower, 0.21 wide, 0.46 tall, 0.45 deep, standing on the floor.
# ---------------------------------------------------------------------------
case_w, case_h, case_d = 0.21, 0.46, 0.45
case_x = 0.42  # case sits to the right of the monitor
case = box(extents=[case_w, case_h, case_d])
at(case, case_x, case_h / 2, 0)
scene.add_geometry(case, node_name="Case", geom_name="Case")

# Front panel inset and a power button
front = box(extents=[case_w * 0.8, case_h * 0.9, 0.006])
at(front, case_x, case_h / 2, case_d / 2 + 0.003)
scene.add_geometry(front, node_name="Case_Front", geom_name="Case_Front")
power = cylinder(radius=0.008, height=0.004, sections=16)
at(power, case_x, case_h - 0.04, case_d / 2 + 0.008)
scene.add_geometry(power, node_name="PowerButton", geom_name="PowerButton")

# Motherboard: standing vertical on the right side wall, facing left (-X)
board_t = 0.004
board = box(extents=[board_t, 0.30, 0.24])
board_x = case_x + case_w / 2 - 0.02
board_y = 0.26
board_z = -0.02
at(board, board_x, board_y, board_z)
scene.add_geometry(board, node_name="Board", geom_name="Board")

# CPU socket, chipset, and the TPM chip with its LED
cpu = box(extents=[0.012, 0.04, 0.04])
at(cpu, board_x - 0.008, board_y + 0.06, board_z + 0.02)
scene.add_geometry(cpu, node_name="CPU", geom_name="CPU")
cooler = cylinder(radius=0.045, height=0.07, sections=24)
at(cooler, board_x - 0.05, board_y + 0.06, board_z + 0.02, rot=([0, 1, 0], 90))
scene.add_geometry(cooler, node_name="CPU_Cooler", geom_name="CPU_Cooler")

tpm = box(extents=[0.006, 0.012, 0.018])
at(tpm, board_x - 0.005, board_y + 0.12, board_z - 0.09)
scene.add_geometry(tpm, node_name="TPM_Chip", geom_name="TPM_Chip")
led = cylinder(radius=0.0025, height=0.004, sections=12)
at(led, board_x - 0.006, board_y + 0.12, board_z - 0.075, rot=([0, 1, 0], 90))
scene.add_geometry(led, node_name="TPM_LED", geom_name="TPM_LED")

# RAM: four thin sticks next to the CPU
for i in range(4):
    stick = box(extents=[0.002, 0.13, 0.03])
    at(stick, board_x - 0.02, board_y + 0.03, board_z + 0.07 + i * 0.012)
    scene.add_geometry(stick, node_name=f"RAM_{i + 1}", geom_name=f"RAM_{i + 1}")

# GPU: horizontal card below the CPU, with a fan ring on its visible face
gpu = box(extents=[0.04, 0.11, 0.26])
gpu_y = board_y - 0.07
at(gpu, board_x - 0.05, gpu_y, board_z)
scene.add_geometry(gpu, node_name="GPU", geom_name="GPU")
for j, dz in enumerate((-0.07, 0.03)):
    ring = annulus(r_min=0.03, r_max=0.038, height=0.004, sections=24)
    at(ring, board_x - 0.072, gpu_y, board_z + dz, rot=([0, 1, 0], 90))
    scene.add_geometry(ring, node_name=f"GPU_Fan_{j + 1}", geom_name=f"GPU_Fan_{j + 1}")

# Drive bay: a cage near the front with four slabs (packages light these up)
cage = box(extents=[0.11, 0.09, 0.12])
cage_x, cage_y, cage_z = case_x - 0.03, 0.09, 0.13
at(cage, cage_x, cage_y, cage_z)
scene.add_geometry(cage, node_name="DriveBay", geom_name="DriveBay")
for k in range(4):
    slab = box(extents=[0.07, 0.008, 0.10])
    at(slab, cage_x, cage_y - 0.03 + k * 0.02, cage_z)
    scene.add_geometry(slab, node_name=f"Drive_{k + 1}", geom_name=f"Drive_{k + 1}")

# PSU block at the bottom rear
psu = box(extents=[0.15, 0.086, 0.14])
at(psu, case_x - 0.01, 0.06, -0.14)
scene.add_geometry(psu, node_name="PSU", geom_name="PSU")

# Rear I/O: a plate with a row of ports and the Ethernet jack
io_plate = box(extents=[0.045, 0.16, 0.004])
io_x, io_y, io_z = board_x - 0.03, board_y + 0.05, -case_d / 2 - 0.002
at(io_plate, io_x, io_y, io_z)
scene.add_geometry(io_plate, node_name="RearIO", geom_name="RearIO")
for p in range(4):
    port = box(extents=[0.012, 0.006, 0.006])
    at(port, io_x - 0.012 + (p % 2) * 0.018, io_y + 0.05 - (p // 2) * 0.012, io_z - 0.004)
    scene.add_geometry(port, node_name=f"USB_{p + 1}", geom_name=f"USB_{p + 1}")
eth = box(extents=[0.016, 0.013, 0.008])
at(eth, io_x, io_y - 0.03, io_z - 0.005)
scene.add_geometry(eth, node_name="Ethernet", geom_name="Ethernet")

# Case fan at the rear top
rear_fan = annulus(r_min=0.045, r_max=0.06, height=0.02, sections=28)
at(rear_fan, case_x - 0.02, case_h - 0.09, -case_d / 2 + 0.012)
scene.add_geometry(rear_fan, node_name="Case_Fan", geom_name="Case_Fan")

# ---------------------------------------------------------------------------
# Monitor: 27 inch 16:9 on a stand, to the left of the case
# ---------------------------------------------------------------------------
mon_w, mon_h, mon_d = 0.61, 0.36, 0.02
mon_x, mon_cy = -0.12, 0.40
bezel = box(extents=[mon_w, mon_h, mon_d])
at(bezel, mon_x, mon_cy, 0)
scene.add_geometry(bezel, node_name="Monitor", geom_name="Monitor")
screen = box(extents=[mon_w - 0.03, mon_h - 0.03, 0.002])
at(screen, mon_x, mon_cy, mon_d / 2 + 0.001)
scene.add_geometry(screen, node_name="Screen", geom_name="Screen")
# A login prompt box and a row of browser tabs on the screen, as separate highlightable nodes
login = box(extents=[0.16, 0.06, 0.002])
at(login, mon_x, mon_cy - 0.02, mon_d / 2 + 0.003)
scene.add_geometry(login, node_name="Screen_Login", geom_name="Screen_Login")
for t in range(5):
    tab = box(extents=[0.09, 0.02, 0.002])
    at(tab, mon_x - 0.22 + t * 0.105, mon_cy + 0.135, mon_d / 2 + 0.003)
    scene.add_geometry(tab, node_name=f"Screen_Tab_{t + 1}", geom_name=f"Screen_Tab_{t + 1}")

neck = box(extents=[0.05, 0.16, 0.03])
at(neck, mon_x, 0.14, -0.04)
scene.add_geometry(neck, node_name="Monitor_Neck", geom_name="Monitor_Neck")
base = box(extents=[0.28, 0.012, 0.18])
at(base, mon_x, 0.006, -0.03)
scene.add_geometry(base, node_name="Monitor_Base", geom_name="Monitor_Base")

# ---------------------------------------------------------------------------
# Backup: a cable from the rear of the case to a small external disk
# ---------------------------------------------------------------------------
cable_pts = np.array([
    [case_x - 0.02, 0.12, -case_d / 2 - 0.01],
    [case_x + 0.10, 0.10, -0.30],
    [case_x + 0.24, 0.06, -0.24],
    [case_x + 0.30, 0.03, -0.16],
])
# The cable is a chain of thin cylinders so it has edges to render
for i in range(len(cable_pts) - 1):
    a, b = cable_pts[i], cable_pts[i + 1]
    seg = trimesh.creation.cylinder(radius=0.004, height=float(np.linalg.norm(b - a)), sections=8,
                                    segment=[a.tolist(), b.tolist()])
    scene.add_geometry(seg, node_name=f"Backup_Cable_{i + 1}", geom_name=f"Backup_Cable_{i + 1}")
ext_disk = box(extents=[0.08, 0.012, 0.11])
at(ext_disk, case_x + 0.34, 0.006, -0.14)
scene.add_geometry(ext_disk, node_name="Backup_Disk", geom_name="Backup_Disk")
ext_led = cylinder(radius=0.003, height=0.004, sections=12)
at(ext_led, case_x + 0.37, 0.014, -0.10)
scene.add_geometry(ext_led, node_name="Backup_LED", geom_name="Backup_LED")

# Floor reference: a thin plane so the orbit has a horizon (optional for the viewer)
floor = box(extents=[1.4, 0.001, 0.9])
at(floor, 0.15, -0.0005, -0.05)
scene.add_geometry(floor, node_name="Floor", geom_name="Floor")

# ---------------------------------------------------------------------------
# Export
# ---------------------------------------------------------------------------
glb_path = os.path.join(OUT, "hyperharbor-machine.glb")
scene.export(glb_path)

# Camera targets per profile section: node to frame, distance, and an orbit tilt.
targets = {
    "overview":  {"node": "Case",       "lookAtOffset": [-0.25, 0.02, 0],  "distance": 1.6,  "elevation": 22, "azimuth": 35},
    "account":   {"node": "Screen_Login","lookAtOffset": [0, 0, 0],        "distance": 0.55, "elevation": 5,  "azimuth": 0},
    "display":   {"node": "Screen",     "lookAtOffset": [0, 0, 0],         "distance": 0.9,  "elevation": 8,  "azimuth": 12},
    "browser":   {"node": "Screen_Tab_3","lookAtOffset": [0, -0.03, 0],    "distance": 0.5,  "elevation": 10, "azimuth": -18},
    "packages":  {"node": "DriveBay",   "lookAtOffset": [0, 0, 0],         "distance": 0.45, "elevation": 18, "azimuth": -50},
    "debloat":   {"node": "DriveBay",   "lookAtOffset": [0, 0, 0],         "distance": 0.5,  "elevation": 30, "azimuth": -70},
    "firmware":  {"node": "TPM_LED",    "lookAtOffset": [0, 0, -0.01],     "distance": 0.16, "elevation": 14, "azimuth": -70},
    "compute":   {"node": "CPU",        "lookAtOffset": [0, 0, 0],         "distance": 0.4,  "elevation": 15, "azimuth": -100},
    "gpu":       {"node": "GPU",        "lookAtOffset": [0, 0, 0],         "distance": 0.5,  "elevation": 10, "azimuth": -110},
    "network":   {"node": "Ethernet",   "lookAtOffset": [0, 0.02, 0],      "distance": 0.3,  "elevation": 8,  "azimuth": 175},
    "backup":    {"node": "Backup_Disk","lookAtOffset": [-0.1, 0.03, -0.05],"distance": 0.6, "elevation": 25, "azimuth": 150},
}
with open(os.path.join(OUT, "targets.json"), "w", encoding="utf-8") as handle:
    json.dump(targets, handle, indent=2)

# Highlight groups: which nodes light up for a section and which react to controls.
highlights = {
    "account":  {"nodes": ["Screen_Login"]},
    "display":  {"nodes": ["Screen"]},
    "browser":  {"nodes": [f"Screen_Tab_{i}" for i in range(1, 6)]},
    "packages": {"nodes": [f"Drive_{i}" for i in range(1, 5)], "perItem": "Drive_{n}"},
    "debloat":  {"nodes": [f"Drive_{i}" for i in range(1, 5)]},
    "firmware": {"nodes": ["TPM_Chip", "TPM_LED"], "blink": "TPM_LED"},
    "compute":  {"nodes": ["CPU", "CPU_Cooler"] + [f"RAM_{i}" for i in range(1, 5)]},
    "gpu":      {"nodes": ["GPU", "GPU_Fan_1", "GPU_Fan_2"], "spin": ["GPU_Fan_1", "GPU_Fan_2"]},
    "network":  {"nodes": ["Ethernet", "RearIO"]},
    "backup":   {"nodes": ["Backup_Disk", "Backup_LED", "Backup_Cable_1", "Backup_Cable_2", "Backup_Cable_3"], "pulse": "Backup_LED"},
}
with open(os.path.join(OUT, "highlights.json"), "w", encoding="utf-8") as handle:
    json.dump(highlights, handle, indent=2)

print("nodes:", len(scene.graph.nodes_geometry))
print("triangles:", sum(len(g.faces) for g in scene.geometry.values()))
print("glb bytes:", os.path.getsize(glb_path))
