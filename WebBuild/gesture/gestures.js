// gestures.js
// Reemplaza a gesture_udp.py en el navegador:
//   cámara -> MediaPipe HandLandmarker -> features (idénticas a landmark_utils.py)
//   -> modelo ONNX (SVM) -> estabilización -> onSend(gesto)
//
// El motor no conoce Unity. La página conecta onSend con unityInstance.SendMessage.

import { HandLandmarker, FilesetResolver }
  from "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14";
import * as ort
  from "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.1/dist/ort.min.mjs";

const TASKS_WASM = "https://cdn.jsdelivr.net/npm/@mediapipe/tasks-vision@0.10.14/wasm";
const ORT_BASE = "https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.1/dist/";
const HAND_MODEL_URL =
  "https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task";

// Mismos valores que gesture_udp.py
export const CONFIG = {
  CONF_THRESHOLD: 0.70,
  FRAMES_TO_ACTIVATE: 3,
  FRAMES_TO_STOP: 2,        // para NINGUNO y REPOSO
  HEARTBEAT_MS: 100,        // GestureReceiver.cs tiene timeout de 0.5 s
  DEFAULT_STATE: "NINGUNO",
  // Se graba a 640x480: MediaPipe normaliza x e y por separado, así que
  // otra relación de aspecto cambiaría las features.
  CAM_WIDTH: 640,
  CAM_HEIGHT: 480,
};

const N_LANDMARKS = 21;
const N_FEATURES = N_LANDMARKS * 3;

/**
 * Port de landmarks_to_features() de landmark_utils.py, aplicando antes el espejo
 * (cv2.flip(frame, 1) en Python => x' = 1 - x).
 *   1. restar la muñeca (landmark 0)
 *   2. dividir entre la norma (x, y) del landmark 9 (si < 1e-6 -> 1)
 *   3. aplanar en orden x, y, z por punto
 */
export function landmarksToFeatures(lm) {
  const out = new Float32Array(N_FEATURES);
  const x0 = 1 - lm[0].x, y0 = lm[0].y, z0 = lm[0].z;
  let scale = Math.hypot((1 - lm[9].x) - x0, lm[9].y - y0);
  if (scale < 1e-6) scale = 1;
  for (let i = 0; i < N_LANDMARKS; i++) {
    out[i * 3]     = ((1 - lm[i].x) - x0) / scale;
    out[i * 3 + 1] = (lm[i].y - y0) / scale;
    out[i * 3 + 2] = (lm[i].z - z0) / scale;
  }
  return out;
}

export class GestureEngine {
  /**
   * @param {object} opts
   *   onSend(gesto: string)  -> se llama al cambiar el gesto y cada HEARTBEAT_MS
   *   onDebug(info)          -> opcional: { raw, conf, stable, ms, landmarks }
   *   modelUrl, classesUrl, handModelUrl -> rutas opcionales
   */
  constructor(opts = {}) {
    this.opts = {
      modelUrl: "gesture/gesture_model.onnx",
      classesUrl: "gesture/classes.json",
      handModelUrl: HAND_MODEL_URL,
      onSend: () => {},
      onDebug: () => {},
      ...opts,
    };
    this.classes = null;
    this.session = null;
    this.landmarker = null;
    this.video = null;
    this.running = false;
    this._busy = false;
    this._raf = 0;
    this._lastVideoTime = -1;

    this.stable = CONFIG.DEFAULT_STATE;
    this.candidate = CONFIG.DEFAULT_STATE;
    this.candidateCount = 0;
    this.lastSent = 0;

    this._tick = this._tick.bind(this);
  }

  /** Carga clases, modelo ONNX y MediaPipe. Llamar antes de start(). */
  async init() {
    ort.env.wasm.wasmPaths = ORT_BASE;
    ort.env.wasm.numThreads = 1; // evita exigir cross-origin isolation

    const resp = await fetch(this.opts.classesUrl);
    if (!resp.ok) throw new Error(`No se pudo cargar ${this.opts.classesUrl}`);
    this.classes = await resp.json();

    this.session = await ort.InferenceSession.create(this.opts.modelUrl, {
      executionProviders: ["wasm"],
    });
    await this.classify(new Float32Array(N_FEATURES)); // calentamiento

    const fileset = await FilesetResolver.forVisionTasks(TASKS_WASM);
    const make = (delegate) => HandLandmarker.createFromOptions(fileset, {
      baseOptions: { modelAssetPath: this.opts.handModelUrl, delegate },
      runningMode: "VIDEO",
      numHands: 1,
      minHandDetectionConfidence: 0.6,
      minHandPresenceConfidence: 0.5,
      minTrackingConfidence: 0.6,
    });
    try {
      this.landmarker = await make("GPU");
    } catch (e) {
      console.warn("[GestureEngine] GPU no disponible, usando CPU:", e);
      this.landmarker = await make("CPU");
    }
  }

  /** Pide la cámara (debe llamarse desde un clic del usuario) y arranca el bucle. */
  async start(videoEl) {
    if (!this.landmarker) throw new Error("Llama a init() antes de start().");
    this.video = videoEl;
    const stream = await navigator.mediaDevices.getUserMedia({
      video: {
        width: { ideal: CONFIG.CAM_WIDTH },
        height: { ideal: CONFIG.CAM_HEIGHT },
        facingMode: "user",
      },
      audio: false,
    });
    this.video.srcObject = stream;
    await this.video.play();

    this.running = true;
    this._send(CONFIG.DEFAULT_STATE);
    this._raf = requestAnimationFrame(this._tick);
  }

  stop() {
    this.running = false;
    cancelAnimationFrame(this._raf);
    const stream = this.video && this.video.srcObject;
    if (stream) stream.getTracks().forEach((t) => t.stop());
    this._send(CONFIG.DEFAULT_STATE);
  }

  /** Devuelve las probabilidades en el orden de classes.json. */
  async classify(features) {
    const input = new ort.Tensor("float32", features, [1, N_FEATURES]);
    const out = await this.session.run({ input }, ["probabilities"]);
    return out.probabilities.data;
  }

  async _tick() {
    if (!this.running) return;
    const v = this.video;
    if (v.readyState >= 2 && v.currentTime !== this._lastVideoTime && !this._busy) {
      this._lastVideoTime = v.currentTime;
      this._busy = true;
      try {
        await this._processFrame();
      } catch (e) {
        console.error("[GestureEngine]", e);
      } finally {
        this._busy = false;
      }
    }
    this._heartbeat();
    this._raf = requestAnimationFrame(this._tick);
  }

  async _processFrame() {
    const t0 = performance.now();
    const res = this.landmarker.detectForVideo(this.video, t0);

    let raw = CONFIG.DEFAULT_STATE, conf = 0, frameLabel = CONFIG.DEFAULT_STATE;
    let landmarks = null;
    if (res.landmarks && res.landmarks.length) {
      landmarks = res.landmarks[0];
      const probs = await this.classify(landmarksToFeatures(landmarks));
      let idx = 0;
      for (let i = 1; i < probs.length; i++) if (probs[i] > probs[idx]) idx = i;
      raw = this.classes[idx];
      conf = probs[idx];
      if (conf >= CONFIG.CONF_THRESHOLD) frameLabel = raw;
    }

    const changed = this._stabilize(frameLabel);
    if (changed) this._send(this.stable);

    this.opts.onDebug({
      raw, conf, stable: this.stable, ms: performance.now() - t0, landmarks,
    });
  }

  // Misma lógica que gesture_udp.py
  _stabilize(frameLabel) {
    if (frameLabel === this.candidate) {
      this.candidateCount++;
    } else {
      this.candidate = frameLabel;
      this.candidateCount = 1;
    }
    const stopLike = this.candidate === CONFIG.DEFAULT_STATE || this.candidate === "REPOSO";
    const needed = stopLike ? CONFIG.FRAMES_TO_STOP : CONFIG.FRAMES_TO_ACTIVATE;
    if (this.candidate !== this.stable && this.candidateCount >= needed) {
      this.stable = this.candidate;
      return true;
    }
    return false;
  }

  _heartbeat() {
    if (performance.now() - this.lastSent >= CONFIG.HEARTBEAT_MS) this._send(this.stable);
  }

  _send(msg) {
    this.lastSent = performance.now();
    this.opts.onSend(msg);
  }

  /**
   * Paso 7 (paridad): corre en el navegador los vectores de parity_sample.json
   * (generados en Python) y compara las probabilidades. No usa cámara ni MediaPipe.
   * Esperado: maxDiff del orden de 1e-5 y ok = true.
   */
  async selfTest(url = "gesture/parity_sample.json") {
    const data = await (await fetch(url)).json();
    const sameOrder = JSON.stringify(data.classes) === JSON.stringify(this.classes);
    let maxDiff = 0;
    const details = [];
    for (const s of data.samples) {
      const p = await this.classify(new Float32Array(s.features));
      let diff = 0;
      for (let i = 0; i < p.length; i++) diff = Math.max(diff, Math.abs(p[i] - s.probabilities[i]));
      maxDiff = Math.max(maxDiff, diff);
      details.push({ label: s.label, diff });
    }
    return { ok: sameOrder && maxDiff < 1e-3, sameOrder, maxDiff, details };
  }
}

// ---------- Utilidad de dibujo (vista previa de la mano) ----------
export const HAND_CONNECTIONS = [
  [0, 1], [1, 2], [2, 3], [3, 4],
  [0, 5], [5, 6], [6, 7], [7, 8],
  [5, 9], [9, 10], [10, 11], [11, 12],
  [9, 13], [13, 14], [14, 15], [15, 16],
  [13, 17], [17, 18], [18, 19], [19, 20],
  [0, 17],
];

/** Dibuja la mano en un canvas 2D. Pasa lm = null para solo limpiar. */
export function drawHand(ctx, lm, w, h) {
  ctx.clearRect(0, 0, w, h);
  if (!lm) return;
  ctx.lineWidth = 2;
  ctx.strokeStyle = "#00e676";
  ctx.beginPath();
  for (const [a, b] of HAND_CONNECTIONS) {
    ctx.moveTo(lm[a].x * w, lm[a].y * h);
    ctx.lineTo(lm[b].x * w, lm[b].y * h);
  }
  ctx.stroke();
  ctx.fillStyle = "#ff1744";
  for (const p of lm) {
    ctx.beginPath();
    ctx.arc(p.x * w, p.y * h, 3, 0, 2 * Math.PI);
    ctx.fill();
  }
}
