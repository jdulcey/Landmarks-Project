using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Cámara 2D (ortográfica) que sigue al jugador. Todo es configurable desde el Inspector.
/// Colócalo en la Main Camera de "MainScene".
/// Se mueve en LateUpdate para actuar después de que el jugador ya se haya movido.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public class CameraFollow2D : MonoBehaviour
{
    public enum FollowMode { Suave, Instantaneo }
    public enum SmallMapAlign { Centro, Abajo, Arriba }

    [Header("Objetivo")]
    [Tooltip("Si se deja vacío se busca un objeto con el tag indicado abajo.")]
    [SerializeField] private Transform target;
    [SerializeField] private string targetTag = "Player";

    [Header("Seguimiento")]
    [SerializeField] private FollowMode followMode = FollowMode.Suave;
    [Tooltip("Desplazamiento de la cámara respecto al jugador (ej. Y = 1 para ver un poco más arriba).")]
    [SerializeField] private Vector2 offset = new Vector2(0f, 1f);
    [Tooltip("Tiempo aprox. en segundos que tarda en alcanzar al jugador. Más alto = más lenta. Solo en modo Suave.")]
    [Min(0f)] [SerializeField] private float smoothTime = 0.2f;
    [SerializeField] private bool followX = true;
    [SerializeField] private bool followY = true;

    [Header("Zona muerta")]
    [Tooltip("Tamaño total (ancho, alto) del área en la que el jugador puede moverse sin que la cámara reaccione. 0 = desactivada.")]
    [SerializeField] private Vector2 deadZone = Vector2.zero;

    [Header("Adelanto (look-ahead)")]
    [Tooltip("Distancia que la cámara se adelanta hacia donde camina el jugador. 0 = desactivado.")]
    [Min(0f)] [SerializeField] private float lookAheadDistance = 1.5f;
    [Tooltip("Qué tan rápido se adelanta o regresa (segundos).")]
    [Min(0.01f)] [SerializeField] private float lookAheadSmoothTime = 0.35f;

    [Header("Zoom")]
    [SerializeField] private bool overrideZoom = false;
    [Min(0.1f)] [SerializeField] private float orthographicSize = 5f;

    [Header("Límites del mapa")]
    [SerializeField] private bool useBounds = false;
    [Tooltip("Opcional: un Collider2D (ej. BoxCollider2D) que cubra el mapa. Si se asigna, ignora los valores manuales.")]
    [SerializeField] private Collider2D boundsCollider;
    [Tooltip("Opcional: el Tilemap del suelo/mapa. Los límites se calculan solos según las celdas pintadas. (Prioridad: Collider > Tilemap > valores manuales)")]
    [SerializeField] private Tilemap boundsTilemap;
    [Tooltip("Agranda (+) o reduce (-) el área límite en todos los lados. Útil para dejar un borde extra o recortar.")]
    [SerializeField] private float boundsMargin = 0f;
    [Tooltip("Ajuste SOLO del límite superior (eje Y). Positivo = el límite sube (la cámara puede ver más arriba). Negativo = baja. No afecta a X.")]
    [SerializeField] private float boundsExtraTop = 0f;
    [Tooltip("Ajuste SOLO del límite inferior (eje Y). Positivo = el límite baja (la cámara puede ver más abajo). Negativo = sube. No afecta a X.")]
    [SerializeField] private float boundsExtraBottom = 0f;
    [Tooltip("Qué hacer cuando la vista de la cámara es MÁS ALTA que el mapa (no hay forma de no ver vacío). Abajo = pegada al suelo, el vacío queda arriba como cielo.")]
    [SerializeField] private SmallMapAlign alignWhenMapIsShorter = SmallMapAlign.Abajo;
    [SerializeField] private Vector2 boundsMin = new Vector2(-20f, -10f);
    [SerializeField] private Vector2 boundsMax = new Vector2(20f, 10f);

    private Camera cam;
    private Vector3 velocity;
    private float lookAheadX;
    private float lookAheadVel;
    private float lookDir;
    private float lastTargetX;

    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (!cam.orthographic)
            Debug.LogWarning("[CameraFollow2D] La cámara no es ortográfica: los límites y el zoom pueden no comportarse como se espera.");
    }

    private void Start()
    {
        if (overrideZoom) cam.orthographicSize = orthographicSize;
        FindTarget();
        SnapToTarget();
    }

    private void LateUpdate()
    {
        if (overrideZoom && !Mathf.Approximately(cam.orthographicSize, orthographicSize))
            cam.orthographicSize = orthographicSize;

        if (target == null)
        {
            FindTarget();
            if (target == null) return;
            SnapToTarget();
        }

        Vector3 goal = ComputeGoal(true);

        Vector3 pos = transform.position;
        if (followMode == FollowMode.Instantaneo || smoothTime <= 0f)
            pos = goal;
        else
            pos = Vector3.SmoothDamp(pos, goal, ref velocity, smoothTime);

        transform.position = ClampToBounds(pos);
    }

    // ------------------------------------------------------------ Cálculo

    private Vector3 ComputeGoal(bool useDeadZone)
    {
        UpdateLookAhead();

        Vector3 cur = transform.position;
        Vector2 desired = (Vector2)target.position + offset + new Vector2(lookAheadX, 0f);

        float gx = cur.x;
        float gy = cur.y;

        if (followX) gx = useDeadZone ? ApplyDeadZone(cur.x, desired.x, deadZone.x * 0.5f) : desired.x;
        if (followY) gy = useDeadZone ? ApplyDeadZone(cur.y, desired.y, deadZone.y * 0.5f) : desired.y;

        return new Vector3(gx, gy, cur.z);
    }

    private static float ApplyDeadZone(float camPos, float desired, float halfSize)
    {
        float d = desired - camPos;
        if (Mathf.Abs(d) <= halfSize) return camPos;
        return desired - Mathf.Sign(d) * halfSize;
    }

    private void UpdateLookAhead()
    {
        if (lookAheadDistance <= 0f)
        {
            lookAheadX = 0f;
            return;
        }

        float dx = target.position.x - lastTargetX;
        lastTargetX = target.position.x;
        if (Mathf.Abs(dx) > 0.0005f) lookDir = Mathf.Sign(dx); // al detenerse conserva la última dirección

        lookAheadX = Mathf.SmoothDamp(lookAheadX, lookDir * lookAheadDistance, ref lookAheadVel, lookAheadSmoothTime);
    }

    private Vector3 ClampToBounds(Vector3 pos)
    {
        if (!useBounds) return pos;

        GetBounds(out Vector2 min, out Vector2 max);

        float halfH = cam.orthographicSize;
        float halfW = halfH * cam.aspect;

        float minX = min.x + halfW, maxX = max.x - halfW;
        float minY = min.y + halfH, maxY = max.y - halfH;

        // Si el mapa es más pequeño que la vista, se centra en vez de clavar la cámara a un lado.
        pos.x = minX > maxX ? (min.x + max.x) * 0.5f : Mathf.Clamp(pos.x, minX, maxX);
        if (minY > maxY)
        {
            switch (alignWhenMapIsShorter)
            {
                case SmallMapAlign.Abajo:  pos.y = minY; break;
                case SmallMapAlign.Arriba: pos.y = maxY; break;
                default:                   pos.y = (min.y + max.y) * 0.5f; break;
            }
        }
        else
        {
            pos.y = Mathf.Clamp(pos.y, minY, maxY);
        }
        return pos;
    }

    private void GetBounds(out Vector2 min, out Vector2 max)
    {
        if (boundsCollider != null)
        {
            Bounds b = boundsCollider.bounds;
            min = b.min;
            max = b.max;
        }
        else if (boundsTilemap != null)
        {
            Bounds lb = boundsTilemap.localBounds;
            Vector3 a = boundsTilemap.transform.TransformPoint(lb.min);
            Vector3 c = boundsTilemap.transform.TransformPoint(lb.max);
            min = Vector2.Min(a, c);
            max = Vector2.Max(a, c);
        }
        else
        {
            min = boundsMin;
            max = boundsMax;
        }

        min -= Vector2.one * boundsMargin;
        max += Vector2.one * boundsMargin;

        // Ajustes independientes del eje Y (no tocan X)
        min.y -= boundsExtraBottom;
        max.y += boundsExtraTop;
    }

    // ------------------------------------------------------------ Utilidades

    private void FindTarget()
    {
        if (target != null || string.IsNullOrEmpty(targetTag)) return;
        try
        {
            GameObject go = GameObject.FindGameObjectWithTag(targetTag);
            if (go != null)
            {
                target = go.transform;
                lastTargetX = target.position.x;
            }
        }
        catch (UnityException)
        {
            Debug.LogWarning($"[CameraFollow2D] El tag '{targetTag}' no existe. Créalo o asigna el objetivo a mano.");
            targetTag = "";
        }
    }

    /// <summary>Coloca la cámara de inmediato sobre el objetivo (sin suavizado).</summary>
    public void SnapToTarget()
    {
        if (target == null) return;
        lastTargetX = target.position.x;
        lookAheadX = 0f;
        lookAheadVel = 0f;
        velocity = Vector3.zero;
        Vector3 goal = ComputeGoal(false);
        transform.position = ClampToBounds(goal);
    }

    /// <summary>Permite cambiar el objetivo desde otro script.</summary>
    public void SetTarget(Transform newTarget, bool snap = false)
    {
        target = newTarget;
        if (snap) SnapToTarget();
    }

    // ------------------------------------------------------------ Gizmos

    private void OnDrawGizmosSelected()
    {
        Camera c = cam != null ? cam : GetComponent<Camera>();

        if (useBounds)
        {
            GetBounds(out Vector2 min, out Vector2 max);
            Gizmos.color = Color.yellow;
            Vector3 center = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, 0f);
            Gizmos.DrawWireCube(center, new Vector3(max.x - min.x, max.y - min.y, 0f));
        }

        if (deadZone.x > 0f || deadZone.y > 0f)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, new Vector3(deadZone.x, deadZone.y, 0f));
        }

        if (c != null && c.orthographic)
        {
            float h = (overrideZoom ? orthographicSize : c.orthographicSize) * 2f;
            Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
            Gizmos.DrawWireCube(transform.position, new Vector3(h * c.aspect, h, 0f));
        }
    }

    private void OnValidate()
    {
        if (boundsMax.x < boundsMin.x) boundsMax.x = boundsMin.x;
        if (boundsMax.y < boundsMin.y) boundsMax.y = boundsMin.y;
    }
}
