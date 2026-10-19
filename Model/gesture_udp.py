"""
Reconocimiento en tiempo real + envío por UDP a Unity (versión de baja latencia).

Mejoras respecto a la versión anterior:
  - Captura de cámara en un hilo aparte que siempre entrega el frame MÁS RECIENTE
    (evita procesar frames viejos acumulados en el búfer de la cámara).
  - Suavizado por frames consecutivos en vez de ventana de votación: activar un
    movimiento tarda FRAMES_TO_ACTIVATE frames; volver a reposo, FRAMES_TO_STOP.
  - Random Forest forzado a 1 hilo (con n_jobs=-1 cada predicción de una sola
    muestra puede costar decenas de ms solo por el reparto entre hilos).
  - MediaPipe en modo ligero (model_complexity=0) y cámara a 640x480.
  - Muestra en pantalla FPS y tiempo de proceso por frame para diagnosticar.

Mensajes enviados (texto plano): REPOSO (puño) | DERECHA | IZQUIERDA | PALMA | NINGUNO
(NINGUNO = sin mano detectada o gesto poco seguro; Unity lo trata como "quieto")
Teclas: q = salir
"""
import socket
import sys
import threading
import time

import cv2
import joblib
import mediapipe as mp
import numpy as np

from landmark_utils import landmarks_to_features

# ---------------- Configuración ----------------
MODEL_PATH = "model/gesture_model.joblib"
CAMERA_INDEX = 0
CAM_WIDTH, CAM_HEIGHT, CAM_FPS = 640, 480, 60
# En Windows, DirectShow suele tener menos latencia que el backend por defecto.
# Si la cámara no abre, ponlo en False.
USE_DSHOW_ON_WINDOWS = True

UDP_IP = "127.0.0.1"
UDP_PORT = 5005
HEARTBEAT = 0.1  # segundos entre reenvíos del estado actual

CONF_THRESHOLD = 0.70       # confianza mínima para aceptar un gesto
FRAMES_TO_ACTIVATE = 3      # frames seguidos iguales para empezar a moverse
FRAMES_TO_STOP = 2          # frames seguidos para volver a REPOSO (más rápido)
DEFAULT_STATE = "NINGUNO"  # sin mano o sin confianza; NO es lo mismo que el puño (REPOSO)
# -----------------------------------------------


class LatestFrameReader:
    """Lee la cámara en un hilo y conserva solo el último frame."""

    def __init__(self, index, width, height, fps):
        backend = cv2.CAP_DSHOW if (USE_DSHOW_ON_WINDOWS and sys.platform == "win32") else cv2.CAP_ANY
        self.cap = cv2.VideoCapture(index, backend)
        self.cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        self.cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        self.cap.set(cv2.CAP_PROP_FPS, fps)
        self.cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        self.lock = threading.Lock()
        self.frame = None
        self.running = True
        self.thread = threading.Thread(target=self._loop, daemon=True)
        self.thread.start()

    def _loop(self):
        while self.running:
            ok, frame = self.cap.read()
            if not ok:
                time.sleep(0.01)
                continue
            with self.lock:
                self.frame = frame

    def read(self):
        """Devuelve el frame nuevo (o None si aún no hay uno nuevo)."""
        with self.lock:
            f, self.frame = self.frame, None
        return f

    def release(self):
        self.running = False
        self.thread.join(timeout=1.0)
        self.cap.release()


def force_single_thread(model):
    """Evita el sobrecoste de paralelismo al predecir 1 muestra por frame."""
    if hasattr(model, "n_jobs"):
        model.n_jobs = 1
    for _, step in getattr(model, "steps", []):
        if hasattr(step, "n_jobs"):
            step.n_jobs = 1


def main():
    bundle = joblib.load(MODEL_PATH)
    model = bundle["model"]
    force_single_thread(model)
    print(f"Modelo cargado: {bundle['name']} | clases: {bundle['classes']}")
    print(f"Enviando a udp://{UDP_IP}:{UDP_PORT}")

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (UDP_IP, UDP_PORT)

    def send(msg: str):
        sock.sendto(msg.encode("utf-8"), target)

    hands = mp.solutions.hands.Hands(
        static_image_mode=False,
        max_num_hands=1,
        model_complexity=0,  # modelo ligero: más rápido
        min_detection_confidence=0.6,
        min_tracking_confidence=0.6,
    )
    drawer = mp.solutions.drawing_utils
    reader = LatestFrameReader(CAMERA_INDEX, CAM_WIDTH, CAM_HEIGHT, CAM_FPS)

    stable = DEFAULT_STATE
    candidate, candidate_count = DEFAULT_STATE, 0
    last_sent = 0.0
    fps, last_frame_time = 0.0, time.perf_counter()
    proc_ms = 0.0
    send(stable)

    try:
        while True:
            frame = reader.read()
            if frame is None:
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    break
                time.sleep(0.001)
                continue

            t0 = time.perf_counter()
            frame = cv2.flip(frame, 1)  # MISMO espejo que en la recolección
            result = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))

            raw_label, conf = DEFAULT_STATE, 0.0
            frame_label = DEFAULT_STATE
            if result.multi_hand_landmarks:
                hl = result.multi_hand_landmarks[0]
                drawer.draw_landmarks(frame, hl, mp.solutions.hands.HAND_CONNECTIONS)
                feats = landmarks_to_features(hl).reshape(1, -1)
                probs = model.predict_proba(feats)[0]
                idx = int(np.argmax(probs))
                raw_label, conf = model.classes_[idx], float(probs[idx])
                if conf >= CONF_THRESHOLD:
                    frame_label = raw_label
            proc_ms = (time.perf_counter() - t0) * 1000.0

            # Suavizado por frames consecutivos (sube rápido, baja más rápido)
            if frame_label == candidate:
                candidate_count += 1
            else:
                candidate, candidate_count = frame_label, 1
            needed = FRAMES_TO_STOP if candidate in (DEFAULT_STATE, "REPOSO") else FRAMES_TO_ACTIVATE
            changed = candidate != stable and candidate_count >= needed
            if changed:
                stable = candidate
                print("Gesto estable ->", stable)

            now = time.time()
            if changed or now - last_sent >= HEARTBEAT:
                send(stable)
                last_sent = now

            # FPS (media móvil)
            t_now = time.perf_counter()
            fps = 0.9 * fps + 0.1 * (1.0 / max(t_now - last_frame_time, 1e-6))
            last_frame_time = t_now

            cv2.putText(frame, f"Crudo: {raw_label} ({conf:.2f})", (10, 30),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 0), 2)
            cv2.putText(frame, f"ENVIANDO: {stable}", (10, 65),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 255, 0), 3)
            cv2.putText(frame, f"FPS {fps:4.1f} | proceso {proc_ms:4.1f} ms", (10, 100),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 2)
            cv2.imshow("Gestos -> Unity (UDP)", frame)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break
    finally:
        send(DEFAULT_STATE)  # al salir, el personaje se queda quieto
        sock.close()
        reader.release()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
