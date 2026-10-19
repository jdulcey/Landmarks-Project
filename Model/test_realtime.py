from collections import Counter, deque

import cv2
import joblib
import mediapipe as mp
import numpy as np

from landmark_utils import landmarks_to_features

MODEL_PATH = "model/gesture_model.joblib"
CAMERA_INDEX = 0
CONF_THRESHOLD = 0.75
WINDOW = 7
MIN_VOTES = 5
DEFAULT_STATE = "REPOSO"


def main():
    bundle = joblib.load(MODEL_PATH)
    model = bundle["model"]
    print(f"Modelo cargado: {bundle['name']} | clases: {bundle['classes']}")

    hands = mp.solutions.hands.Hands(
        static_image_mode=False,
        max_num_hands=1,
        min_detection_confidence=0.6,
        min_tracking_confidence=0.6,
    )
    drawer = mp.solutions.drawing_utils
    cap = cv2.VideoCapture(CAMERA_INDEX)

    history = deque(maxlen=WINDOW)
    stable = DEFAULT_STATE

    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame = cv2.flip(frame, 1)
        result = hands.process(cv2.cvtColor(frame, cv2.COLOR_BGR2RGB))

        raw_label, conf = DEFAULT_STATE, 0.0
        if result.multi_hand_landmarks:
            hl = result.multi_hand_landmarks[0]
            drawer.draw_landmarks(frame, hl, mp.solutions.hands.HAND_CONNECTIONS)
            feats = landmarks_to_features(hl).reshape(1, -1)
            probs = model.predict_proba(feats)[0]
            idx = int(np.argmax(probs))
            raw_label, conf = model.classes_[idx], float(probs[idx])
            frame_label = raw_label if conf >= CONF_THRESHOLD else DEFAULT_STATE
        else:
            frame_label = DEFAULT_STATE

        history.append(frame_label)
        label, votes = Counter(history).most_common(1)[0]
        if votes >= MIN_VOTES and label != stable:
            stable = label
            print("Gesto estable ->", stable)

        cv2.putText(frame, f"Crudo: {raw_label} ({conf:.2f})", (10, 30),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 0), 2)
        cv2.putText(frame, f"ESTABLE: {stable}", (10, 65),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 255, 0), 3)
        cv2.imshow("Prueba en tiempo real", frame)
        if cv2.waitKey(1) & 0xFF == ord("q"):
            break

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
