import os
import sys

import joblib
import numpy as np
import pandas as pd
from sklearn.ensemble import RandomForestClassifier
from sklearn.metrics import classification_report, confusion_matrix, f1_score
from sklearn.model_selection import GroupShuffleSplit
from sklearn.neural_network import MLPClassifier
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from sklearn.svm import SVC

from landmark_utils import CLASSES, MIRROR_LABEL, mirror_features

DATA_PATH = "data/landmarks.csv"
MODEL_PATH = "model/gesture_model.joblib"
SEED = 42
rng = np.random.default_rng(SEED)


def augment(X, y_labels, noise_std=0.01):
    X_m = np.array([mirror_features(x) for x in X])
    y_m = np.array([MIRROR_LABEL[l] for l in y_labels])
    X_all = np.vstack([X, X_m])
    y_all = np.concatenate([y_labels, y_m])
    X_noisy = X_all + rng.normal(0, noise_std, X_all.shape)
    return np.vstack([X_all, X_noisy]), np.concatenate([y_all, y_all])


def main():
    if not os.path.exists(DATA_PATH):
        sys.exit(f"No existe {DATA_PATH}. Ejecuta primero collect_data.py")

    df = pd.read_csv(DATA_PATH)
    feature_cols = [c for c in df.columns if c.startswith("f")]
    X = df[feature_cols].to_numpy(dtype=np.float32)
    y = df["label"].to_numpy()
    groups = df["session"].to_numpy()

    print("Muestras por clase:\n", df["label"].value_counts().to_string(), "\n")
    n_sessions = len(np.unique(groups))
    print(f"Sesiones distintas: {n_sessions}")

    if n_sessions >= 3:
        splitter = GroupShuffleSplit(n_splits=1, test_size=0.25, random_state=SEED)
        train_idx, test_idx = next(splitter.split(X, y, groups))
    else:
        print("AVISO: hay menos de 3 sesiones; se usará una división aleatoria, que da una "
              "precisión OPTIMISTA. Graba más sesiones (otra hora, luz, distancia, persona).")
        from sklearn.model_selection import train_test_split
        train_idx, test_idx = train_test_split(
            np.arange(len(y)), test_size=0.25, random_state=SEED, stratify=y)

    X_train, y_train = X[train_idx], y[train_idx]
    X_test, y_test = X[test_idx], y[test_idx]
    print(f"Train: {len(y_train)} | Test: {len(y_test)}\n")

    X_train_aug, y_train_aug = augment(X_train, y_train)

    models = {
        "RandomForest": RandomForestClassifier(n_estimators=200, random_state=SEED, n_jobs=-1),
        "SVM": make_pipeline(StandardScaler(), SVC(kernel="rbf", C=5, probability=True, random_state=SEED)),
        "MLP": make_pipeline(StandardScaler(), MLPClassifier(
            hidden_layer_sizes=(64, 32), alpha=1e-3, max_iter=500,
            random_state=SEED)),
    }

    best_name, best_model, best_f1 = None, None, -1.0
    for name, model in models.items():
        model.fit(X_train_aug, y_train_aug)
        pred = model.predict(X_test)
        f1 = f1_score(y_test, pred, average="macro")
        print(f"{name}: F1 macro en test = {f1:.4f}")
        if f1 > best_f1:
            best_name, best_model, best_f1 = name, model, f1

    print(f"\nMejor modelo: {best_name}\n")
    pred = best_model.predict(X_test)
    print(classification_report(y_test, pred, labels=CLASSES, zero_division=0))
    print("Matriz de confusión (filas = real, columnas = predicho):")
    print("Orden:", CLASSES)
    print(confusion_matrix(y_test, pred, labels=CLASSES))

    X_full_aug, y_full_aug = augment(X, y)
    best_model.fit(X_full_aug, y_full_aug)

    os.makedirs("model", exist_ok=True)
    joblib.dump({"model": best_model, "classes": CLASSES, "name": best_name}, MODEL_PATH)
    print(f"\nModelo guardado en {MODEL_PATH}")


if __name__ == "__main__":
    main()
