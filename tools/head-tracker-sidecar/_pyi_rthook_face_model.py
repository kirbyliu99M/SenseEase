import os, sys
# Prefer an exe-adjacent face_landmarker.task (user-replaceable),
# else fall through to the bundled _internal/ copy via resolve_model_path()'s
# sibling-of-__file__ probe.
_exe_dir = os.path.dirname(sys.executable)
_adj = os.path.join(_exe_dir, 'face_landmarker.task')
if os.path.isfile(_adj) and not os.environ.get('SENSEEASE_FACE_LANDMARKER_MODEL'):
    os.environ['SENSEEASE_FACE_LANDMARKER_MODEL'] = _adj
