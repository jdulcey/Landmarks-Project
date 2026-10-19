using UnityEngine;

/// <summary>
/// Fondo con parallax. El objeto padre se mantiene centrado en la cámara (X e Y) y,
/// opcionalmente, se escala solo para cubrir siempre toda la vista.
/// Cada hijo (con Renderer) desplaza la textura según su profundidad (eje Z).
/// Requisitos: las texturas deben tener Wrap Mode = Repeat.
/// </summary>
[DefaultExecutionOrder(100)] // corre DESPUÉS de CameraFollow2D para no ir un frame atrasado
public class ParallaxMovement : MonoBehaviour
{
    public enum FitMode { Ninguno, CubrirCamara }

    [Header("Parallax")]
    [Range(0.01f, 1f)] public float parallaxSpeed = 0.01f;
    [Tooltip("Propiedad de textura del shader. Si no existe en el material se prueba con _BaseMap (URP).")]
    [SerializeField] private string textureProperty = "_MainTex";

    [Header("Posición respecto a la cámara")]
    [SerializeField] private bool followX = true;
    [SerializeField] private bool followY = true;
    [Tooltip("Desplazamiento del fondo respecto al centro de la cámara. (0,0) = centrado.")]
    [SerializeField] private Vector2 offset = Vector2.zero;

    [Header("Tamaño")]
    [Tooltip("CubrirCamara: escala el fondo automáticamente para que siempre llene la vista.")]
    [SerializeField] private FitMode fitMode = FitMode.CubrirCamara;
    [Tooltip("1.05 = 5% más grande que la vista, para evitar bordes visibles.")]
    [Min(1f)] [SerializeField] private float coverMargin = 1.05f;

    private Camera camComp;
    private Transform cam;
    private Vector3 camStartPos;
    private bool initialized;
    private float startZ;

    private Renderer[] renderers;
    private Material[] mat;
    private string[] texProp;
    private float[] backSpeed;

    // Datos base para centrar y escalar
    private bool hasBounds;
    private Vector3 baseScale;
    private Vector2 baseSize;
    private Vector2 baseCenterOffset;

    private void Start()
    {
        camComp = Camera.main;
        if (camComp == null)
        {
            Debug.LogWarning("[ParallaxMovement] No se encontró una cámara con el tag MainCamera.");
            enabled = false;
            return;
        }
        cam = camComp.transform;
        startZ = transform.position.z;

        int count = transform.childCount;
        renderers = new Renderer[count];
        mat = new Material[count];
        texProp = new string[count];
        backSpeed = new float[count];

        for (int i = 0; i < count; i++)
        {
            renderers[i] = transform.GetChild(i).GetComponent<Renderer>();
            if (renderers[i] == null) continue;

            mat[i] = renderers[i].material;
            texProp[i] = mat[i].HasProperty(textureProperty) ? textureProperty
                       : mat[i].HasProperty("_BaseMap") ? "_BaseMap"
                       : textureProperty;
        }

        BackSpeedCalculate(count);
        CaptureBase();
    }

    private void BackSpeedCalculate(int count)
    {
        float farthest = 0f;
        for (int i = 0; i < count; i++)
        {
            float d = transform.GetChild(i).position.z - cam.position.z;
            if (d > farthest) farthest = d;
        }

        for (int i = 0; i < count; i++)
        {
            float d = transform.GetChild(i).position.z - cam.position.z;
            backSpeed[i] = farthest > 0f ? 1f - d / farthest : 0f;
        }
    }

    /// <summary>Guarda el tamaño y el centro visual del fondo con su escala original.</summary>
    private void CaptureBase()
    {
        Renderer[] all = GetComponentsInChildren<Renderer>();
        if (all.Length == 0) return;

        Bounds b = all[0].bounds;
        for (int i = 1; i < all.Length; i++) b.Encapsulate(all[i].bounds);

        baseSize = b.size;
        baseCenterOffset = b.center - transform.position;
        baseScale = transform.localScale;
        hasBounds = baseSize.x > 0.0001f && baseSize.y > 0.0001f;
    }

    private void LateUpdate()
    {
        if (cam == null) return;

        if (!initialized)
        {
            camStartPos = cam.position;
            initialized = true;
        }

        // 1) Tamaño: cubrir siempre la vista de la cámara
        float factor = 1f;
        if (hasBounds && fitMode == FitMode.CubrirCamara)
        {
            Vector2 view = GetViewSize(Mathf.Abs(startZ - cam.position.z));
            factor = Mathf.Max(view.x / baseSize.x, view.y / baseSize.y) * coverMargin;
            transform.localScale = baseScale * factor;
        }
        else if (hasBounds)
        {
            factor = transform.localScale.x / (baseScale.x != 0f ? baseScale.x : 1f);
        }

        // 2) Posición: el centro VISUAL del fondo queda en el centro de la cámara (+ offset)
        Vector2 centerOffset = hasBounds ? baseCenterOffset * factor : Vector2.zero;
        Vector3 p = transform.position;
        if (followX) p.x = cam.position.x + offset.x - centerOffset.x;
        if (followY) p.y = cam.position.y + offset.y - centerOffset.y;
        p.z = startZ;
        transform.position = p;

        // 3) Parallax por desplazamiento de textura
        float distance = cam.position.x - camStartPos.x;
        for (int i = 0; i < mat.Length; i++)
        {
            if (mat[i] == null) continue;
            float speed = backSpeed[i] * parallaxSpeed;
            mat[i].SetTextureOffset(texProp[i], new Vector2(distance * speed, 0f));
        }
    }

    private Vector2 GetViewSize(float distance)
    {
        float h = camComp.orthographic
            ? camComp.orthographicSize * 2f
            : 2f * distance * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        return new Vector2(h * camComp.aspect, h);
    }
}