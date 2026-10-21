"""
Exporta model/gesture_model.joblib a ONNX para usarlo en el navegador con onnxruntime-web.

Instalar una sola vez:
    pip install skl2onnx onnxruntime

Genera en model/:
    gesture_model.onnx   -> el modelo (entrada float32 [N, 63], salida "probabilities")
    classes.json         -> orden EXACTO de las clases del vector de probabilidades
    parity_sample.json   -> un vector de features por clase + probabilidades esperadas,
                            para comprobar luego que JS da lo mismo que Python
"""
import json
import os
import sys

import joblib
import numpy as np
import onnxruntime as ort
import pandas as pd
from skl2onnx import convert_sklearn
from skl2onnx.common.data_types import FloatTensorType

MODEL_PATH = "model/gesture_model.joblib"
ONNX_PATH = "model/gesture_model.onnx"
CLASSES_PATH = "model/classes.json"
PARITY_PATH = "model/parity_sample.json"
DATA_PATH = "data/landmarks.csv"

N_FEATURES = 63
MAX_ALLOWED_DIFF = 1e-3


def final_estimator(model):
    """Si es un Pipeline devuelve su último paso (el clasificador); si no, el propio modelo."""
    steps = getattr(model, "steps", None)
    return steps[-1][1] if steps else model


def main():
    if not os.path.exists(MODEL_PATH):
        sys.exit(f"No existe {MODEL_PATH}. Ejecuta primero train_model.py")

    bundle = joblib.load(MODEL_PATH)
    model = bundle["model"]
    print(f"Modelo: {bundle['name']}")

    # IMPORTANTE: sklearn ordena classes_ alfabéticamente, no como la lista CLASSES.
    # El vector de probabilidades de ONNX sigue ESTE orden.
    classes = [str(c) for c in model.classes_]
    print("Orden de clases del modelo:", classes)

    # zipmap=False hace que "probabilities" sea un tensor [N, n_clases] y no una lista de dicts.
    # Se aplica al clasificador final (dentro del Pipeline si lo hay).
    est = final_estimator(model)
    onx = convert_sklearn(
        model,
        initial_types=[("input", FloatTensorType([None, N_FEATURES]))],
        options={id(est): {"zipmap": False}},
        # Opset conservador: lo soporta cualquier onnxruntime-web reciente.
        target_opset={"": 17, "ai.onnx.ml": 3},
    )
    print("Opsets del modelo:", [(o.domain or "ai.onnx", o.version) for o in onx.opset_import])

    os.makedirs("model", exist_ok=True)
    with open(ONNX_PATH, "wb") as f:
        f.write(onx.SerializeToString())
    with open(CLASSES_PATH, "w", encoding="utf-8") as f:
        json.dump(classes, f, ensure_ascii=False)
    print(f"Guardado: {ONNX_PATH} ({os.path.getsize(ONNX_PATH) / 1024:.1f} KB) y {CLASSES_PATH}")

    # ---------- Verificación: sklearn vs ONNX ----------
    sess = ort.InferenceSession(ONNX_PATH, providers=["CPUExecutionProvider"])
    print("Entradas ONNX:", [(i.name, i.shape, i.type) for i in sess.get_inputs()])
    print("Salidas ONNX:", [(o.name, o.shape, o.type) for o in sess.get_outputs()])

    if not os.path.exists(DATA_PATH):
        print(f"AVISO: no existe {DATA_PATH}; se omite la verificación y parity_sample.json.")
        return

    df = pd.read_csv(DATA_PATH)
    feature_cols = [c for c in df.columns if c.startswith("f")]
    X = df[feature_cols].to_numpy(dtype=np.float32)
    y = df["label"].to_numpy()

    rng = np.random.default_rng(0)
    idx = rng.choice(len(X), size=min(1000, len(X)), replace=False)
    Xv = X[idx]

    p_sk = model.predict_proba(Xv)
    p_onnx = sess.run(["probabilities"], {"input": Xv})[0]
    max_diff = float(np.abs(p_sk - p_onnx).max())
    agree = float((p_sk.argmax(1) == p_onnx.argmax(1)).mean())
    print(f"\nDiferencia máxima de probabilidades sklearn vs ONNX: {max_diff:.6f}")
    print(f"Coincidencia de clase predicha: {agree * 100:.2f}%")
    if max_diff > MAX_ALLOWED_DIFF:
        sys.exit("ERROR: la diferencia es demasiado grande; no uses este .onnx.")
    print("OK: el ONNX es equivalente al modelo de sklearn.")

    # ---------- Muestras de paridad para comparar con JS (una por clase) ----------
    samples = []
    for c in classes:
        rows = np.where(y == c)[0]
        if len(rows) == 0:
            continue
        x = X[rows[len(rows) // 2]]
        p = sess.run(["probabilities"], {"input": x.reshape(1, -1)})[0][0]
        samples.append({"label": c, "features": x.tolist(), "probabilities": p.tolist()})
    with open(PARITY_PATH, "w", encoding="utf-8") as f:
        json.dump({"classes": classes, "samples": samples}, f)
    print(f"Guardado: {PARITY_PATH} ({len(samples)} muestras)")


if __name__ == "__main__":
    main()
