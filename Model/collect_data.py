"""
Recolección de datos.

Teclas:
  1 = grabar REPOSO (puño cerrado)
  2 = grabar DERECHA (mano señalando a la derecha)
  3 = grabar IZQUIERDA (mano señalando a la izquierda)
  4 = grabar PALMA (palma abierta, dedos hacia arriba, mirando a la cámara)
  5 = grabar DOS_DEDOS (símbolo de paz: índice y medio arriba, palma a la cámara)
  6 = grabar UN_DEDO (solo el índice arriba, palma a la cámara)
  p = pausar grabación
  q = salir

Cada ejecución del script es una "sesión" (se guarda un id de sesión por fila).
Esto permite luego validar el modelo con sesiones que nunca vio al entrenar.

Dirección: la imagen se muestra como espejo (como un espejo real). "Derecha"
significa hacia TU derecha, que es la derecha de la pantalla.
"""
import csv
import os
import time
from datetime import datetime

import cv2
import mediapipe as mp

from landmark_utils import CLASSES, N_FEATURES, landmarks_to_features

DATA_PATH = "data/landmarks.csv"
CAMERA_INDEX = 0

KEY_TO_LABEL = {ord("1"): "REPOSO", ord("2"): "DERECHA", ord("3"): "IZQUIERDA", ord("4"): "PALMA",
                ord("5"): "DOS_DEDOS", ord("6"): "UN_DEDO"}


def count_existing():
    counts = {c: 0 for c in CLASSES}
    if os.path.exists(DATA_PATH):
        with open(DATA_PATH, newline="") as f:
            reader = csv.reader(f)
            next(reader, None)
            for row in reader:
                if row and row[0] in counts:
                    counts[row[0]] += 1
    return counts


def main():
    os.makedirs("data", exist_ok=True)
    new_file = not os.path.exists(DATA_PATH)
    counts = count_existing()
    session_id = datetime.now().strftime("%Y%m%d_%H%M%S")

    f = open(DATA_PATH, "a", newline="")
    writer = csv.writer(f)
    if new_file:
        writer.writerow(["label", "session"] + [f"f{i}" for i in range(N_FEATURES)])

    hands = mp.solutions.hands.Hands(
        static_image_mode=False,
        max_num_hands=1,
        min_detection_confidence=0.6,
        min_tracking_confidence=0.6,
    )
    drawer = mp.solutions.drawing_utils

    cap = cv2.VideoCapture(CAMERA_INDEX)
    current_label = None
    last_save = 0.0
    SAVE_INTERVAL = 0.05  # ~20 muestras/seg (evita guardar frames casi idénticos)

    print(f"Sesión: {session_id}. Presiona 1-6 para grabar, p pausa, q salir.")
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame = cv2.flip(frame, 1)  # espejo
        result = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))

        hand_detected = bool(result.multi_hand_landmarks)
        if hand_detected:
            hl = result.multi_hand_landmarks[0]
            drawer.draw_landmarks(frame, hl, mp.solutions.hands.HAND_CONNECTIONS)
            now = time.time()
            if current_label and now - last_save >= SAVE_INTERVAL:
                writer.writerow([current_label, session_id] + landmarks_to_features(hl).tolist())
                counts[current_label] += 1
                last_save = now

        status = f"GRABANDO: {current_label}" if current_label else "PAUSADO"
        color = (0, 0, 255) if current_label else (200, 200, 200)
        cv2.putText(frame, status, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.9, color, 2)
        if not hand_detected:
            cv2.putText(frame, "Sin mano detectada", (10, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 165, 255), 2)
        y = 95
        for i, c in enumerate(CLASSES):
            cv2.putText(frame, f"[{i + 1}] {c}: {counts[c]}", (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
            y += 25

        cv2.imshow("Recoleccion de datos", frame)
        key = cv2.waitKey(1) & 0xFF
        if key == ord("q"):
            break
        elif key == ord("p"):
            current_label = None
        elif key in KEY_TO_LABEL:
            current_label = KEY_TO_LABEL[key]

    f.close()
    cap.release()
    cv2.destroyAllWindows()
    print("Conteo final:", counts)


if __name__ == "__main__":
    main()
