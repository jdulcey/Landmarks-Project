import numpy as np

CLASSES = ["REPOSO", "DERECHA", "IZQUIERDA", "PALMA", "DOS_DEDOS", "UN_DEDO"]
CLASS_TO_ID = {name: i for i, name in enumerate(CLASSES)}

MIRROR_LABEL = {"REPOSO": "REPOSO", "DERECHA": "IZQUIERDA", "IZQUIERDA": "DERECHA", "PALMA": "PALMA",
                "DOS_DEDOS": "DOS_DEDOS", "UN_DEDO": "UN_DEDO"}

N_LANDMARKS = 21
N_FEATURES = N_LANDMARKS * 3


def landmarks_to_features(hand_landmarks) -> np.ndarray:
    pts = np.array([[lm.x, lm.y, lm.z] for lm in hand_landmarks.landmark], dtype=np.float32)
    pts -= pts[0]
    scale = np.linalg.norm(pts[9, :2])
    if scale < 1e-6:
        scale = 1.0
    pts /= scale
    return pts.flatten()


def mirror_features(features: np.ndarray) -> np.ndarray:
    f = features.reshape(-1, 3).copy()
    f[:, 0] *= -1.0
    return f.flatten()
