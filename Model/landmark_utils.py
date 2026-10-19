"""
Código compartido entre recolección, entrenamiento y prueba en tiempo real.
IMPORTANTE: usar SIEMPRE la misma función de features en los tres sitios,
si no, el modelo recibirá en vivo datos distintos a los que vio al entrenar.
"""
import numpy as np

# Clases (el orden define el id numérico). Estos nombres son las palabras clave
# que más adelante se enviarán a Unity por UDP.
CLASSES = ["REPOSO", "DERECHA", "IZQUIERDA", "PALMA", "DOS_DEDOS", "UN_DEDO"]  # REPOSO = puño cerrado
CLASS_TO_ID = {name: i for i, name in enumerate(CLASSES)}

# Al reflejar horizontalmente una mano, DERECHA <-> IZQUIERDA (REPOSO no cambia).
MIRROR_LABEL = {"REPOSO": "REPOSO", "DERECHA": "IZQUIERDA", "IZQUIERDA": "DERECHA", "PALMA": "PALMA",
                "DOS_DEDOS": "DOS_DEDOS", "UN_DEDO": "UN_DEDO"}

N_LANDMARKS = 21
N_FEATURES = N_LANDMARKS * 3


def landmarks_to_features(hand_landmarks) -> np.ndarray:
    """
    Convierte los 21 landmarks de MediaPipe en un vector de 63 valores.

    - Traslación: se resta la muñeca (landmark 0) -> invariante a la posición en el cuadro.
    - Escala: se divide por la distancia muñeca -> base del dedo medio (landmark 9)
      -> invariante a qué tan cerca/lejos está la mano de la cámara.
    - NO se normaliza la rotación a propósito: la orientación de la mano
      (apuntando a izquierda o derecha) es justamente lo que distingue los gestos.
    """
    pts = np.array([[lm.x, lm.y, lm.z] for lm in hand_landmarks.landmark], dtype=np.float32)
    pts -= pts[0]
    scale = np.linalg.norm(pts[9, :2])
    if scale < 1e-6:
        scale = 1.0
    pts /= scale
    return pts.flatten()


def mirror_features(features: np.ndarray) -> np.ndarray:
    """Refleja un vector de features horizontalmente (invierte el signo de x)."""
    f = features.reshape(-1, 3).copy()
    f[:, 0] *= -1.0
    return f.flatten()
