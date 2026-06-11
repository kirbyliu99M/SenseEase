# -*- mode: python ; coding: utf-8 -*-
#
# head_tracker.spec — PyInstaller --onedir bundle for SenseEase head-tracker sidecar.
#
# Build:
#   .\.venv\Scripts\pyinstaller.exe head_tracker.spec --noconfirm --clean
#
# Output (PyInstaller 6 --onedir layout):
#   tools/head-tracker-sidecar/dist/head_tracker/
#     head_tracker.exe              (~1 MB bootstrap)
#     face_landmarker.task          (~4 MB — bundled by this spec via B1a PKG-06;
#                                    also accepted exe-adjacent for user-replaceable
#                                    model swaps via _pyi_rthook_face_model.py)
#     _internal/                    (~220 MB — Python runtime + mediapipe + cv2 + numpy;
#                                    NOTE: PyInstaller 6 places all runtime data here,
#                                    NOT exe-adjacent as in older versions)

import os
from PyInstaller.utils.hooks import collect_all

block_cipher = None

# ── Inputs ────────────────────────────────────────────────────────────────────
script_dir = os.path.abspath(os.path.dirname(SPEC))
entry_script = os.path.join(script_dir, 'head_tracker.py')

# Project root holds the canonical face_landmarker.task (untracked, ~3.6 MB).
# Probe project-root first, then the sidecar dir, so either layout works.
project_root_model = os.path.normpath(os.path.join(script_dir, '..', '..', 'face_landmarker.task'))
sidecar_local_model = os.path.join(script_dir, 'face_landmarker.task')
model_src = project_root_model if os.path.isfile(project_root_model) else sidecar_local_model
if not os.path.isfile(model_src):
    raise SystemExit(
        f"face_landmarker.task not found at either:\n"
        f"  {project_root_model}\n  {sidecar_local_model}\n"
        f"Download from https://storage.googleapis.com/mediapipe-models/face_landmarker/"
        f"face_landmarker/float16/1/face_landmarker.task before building."
    )

# ── Mediapipe + OpenCV: collect everything they need ─────────────────────────
mp_datas, mp_binaries, mp_hidden = collect_all('mediapipe')
cv_datas, cv_binaries, cv_hidden = collect_all('cv2')

hidden_imports = sorted(set(mp_hidden + cv_hidden + [
    'mediapipe.tasks.python.vision',
    'mediapipe.tasks.python.core',
    'mediapipe.tasks.python.core.base_options',
    'mediapipe.python._framework_bindings',
    'mediapipe.framework.formats',
    'psutil',
]))

# face_landmarker.task bundled into _internal/ (sibling-of-__file__ probe lands here);
# runtime hook ALSO points to exe-adjacent copy if user drops one.
datas = mp_datas + cv_datas + [
    (model_src, '.'),
]

binaries = mp_binaries + cv_binaries

# ── Runtime hook: pin SENSEEASE_FACE_LANDMARKER_MODEL ────────────────────────
runtime_hook = os.path.join(script_dir, '_pyi_rthook_face_model.py')

# ── Excludes ─────────────────────────────────────────────────────────────────
excludes = [
    'PyQt5', 'PyQt6', 'PySide2', 'PySide6',
    'tkinter', 'IPython', 'pytest',
    # NOTE: matplotlib is NOT excluded. mediapipe/python/solutions/drawing_utils.py
    # imports matplotlib at the top of the module; mediapipe's __init__.py force-
    # loads solutions/__init__.py which loads drawing_utils. Excluding matplotlib
    # causes ModuleNotFoundError on `import mediapipe`. Cost: ~30 MB bundle size.
    # NOTE: mediapipe.python.solutions.* (hands/pose/holistic/objectron/etc.) are
    # NOT excluded either. mediapipe/python/solutions/__init__.py imports ALL of
    # them at module-load time, each pulling its own deps (attr, drawing_styles,
    # etc.). Excluding the modules makes PyInstaller skip their deps too, which
    # then breaks `import mediapipe`. The Tasks API path we use doesn't EXECUTE
    # these solutions, but they must IMPORT cleanly. Cost: ~30-40 MB.
]

# ── Analysis ─────────────────────────────────────────────────────────────────
exe_name = 'head_tracker'

a = Analysis(
    [entry_script],
    pathex=[script_dir],
    binaries=binaries,
    datas=datas,
    hiddenimports=hidden_imports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[runtime_hook],
    excludes=excludes,
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

# Drop heaviest non-face mediapipe assets + cv2 haar cascades (saves ~25-35 MB).
def _is_dead_weight(item):
    src = item[0]
    bad = ('hand_landmark', 'pose_landmark', 'pose_detection', 'holistic_landmark',
           'objectron', 'selfie_segmentation', 'palm_detection', 'iris_landmark')
    norm = src.replace('\\', '/').lower()
    if any(f'/modules/{b}/' in norm for b in bad):
        return True
    if 'cv2/data/haarcascade' in norm:
        return True
    return False

a.datas    = [d for d in a.datas    if not _is_dead_weight(d)]
a.binaries = [b for b in a.binaries if not _is_dead_weight(b)]

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name=exe_name,
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,                # UPX trips Windows Defender on packed mediapipe pyds.
    console=True,             # stdout JSON is the IPC channel — keep console attached.
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)

coll = COLLECT(
    exe,
    a.binaries,
    a.zipfiles,
    a.datas,
    strip=False,
    upx=False,
    upx_exclude=[],
    name=exe_name,            # → dist/head_tracker/
)
