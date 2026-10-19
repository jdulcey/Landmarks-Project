using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Mueve al personaje según el gesto recibido (DERECHA / IZQUIERDA / REPOSO) y
/// controla las animaciones Idle / Walk mediante un parámetro bool del Animator.
/// El teclado (A/D o flechas) funciona como respaldo para depurar sin el modelo.
/// Requiere Rigidbody2D en el mismo objeto.
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class PlayerController2D : MonoBehaviour
{
    [Header("Entrada")]
    [SerializeField] private GestureReceiver receiver;
    [SerializeField] private bool allowKeyboardFallback = true;

    [Header("Movimiento")]
    [SerializeField] private float speed = 5f;

    [Header("Animación")]
    [Tooltip("Si se deja vacío se busca en este objeto y en sus hijos.")]
    [SerializeField] private Animator animator;
    [Tooltip("Si se deja vacío se busca en este objeto y en sus hijos.")]
    [SerializeField] private SpriteRenderer spriteRenderer;
    [Tooltip("Nombre EXACTO del parámetro Bool del Animator (true = Walk, false = Idle).")]
    [SerializeField] private string walkBoolParameter = "IsWalking";
    [Tooltip("Marca esto si tus sprites originales miran hacia la derecha.")]
    [SerializeField] private bool spriteFacesRight = true;

    private Rigidbody2D rb;
    private float moveX;
    private int walkParamHash;
    private bool hasWalkParam;

    private void Awake()
    {
        rb = GetComponent<Rigidbody2D>();

        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (spriteRenderer == null) spriteRenderer = GetComponentInChildren<SpriteRenderer>();

        walkParamHash = Animator.StringToHash(walkBoolParameter);
        hasWalkParam = FindBoolParameter(animator, walkParamHash);

        if (animator == null)
            Debug.LogWarning("[PlayerController2D] No se encontró un Animator: el personaje se moverá sin animación.");
        else if (!hasWalkParam)
            Debug.LogWarning($"[PlayerController2D] El Animator no tiene un parámetro Bool llamado '{walkBoolParameter}'. " +
                             "Créalo en la pestaña Parameters del Animator y úsalo en las transiciones Idle <-> Walk.");
    }

    private void Update()
    {
        moveX = 0f;

        if (receiver == null) receiver = GestureReceiver.Instance; // receptor persistente entre escenas

        if (receiver != null)
        {
            switch (receiver.CurrentGesture)
            {
                case "DERECHA":   moveX = 1f;  break;
                case "IZQUIERDA": moveX = -1f; break;
                default:          moveX = 0f;  break; // REPOSO o cualquier mensaje desconocido
            }
        }

        // Respaldo por teclado (A/D o flechas). Funciona con el Input System nuevo y con el antiguo.
        if (allowKeyboardFallback && moveX == 0f)
            moveX = ReadKeyboard();

        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        bool walking = moveX != 0f;

        if (hasWalkParam)
            animator.SetBool(walkParamHash, walking);

        // Solo se voltea al moverse: al detenerse conserva la última dirección.
        if (spriteRenderer != null && walking)
            spriteRenderer.flipX = spriteFacesRight ? moveX < 0f : moveX > 0f;
    }

    private void FixedUpdate()
    {
#if UNITY_6000_0_OR_NEWER
        rb.linearVelocity = new Vector2(moveX * speed, rb.linearVelocity.y);
#else
        rb.velocity = new Vector2(moveX * speed, rb.velocity.y);
#endif
    }

    private float ReadKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return 0f;
        float x = 0f;
        if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) x -= 1f;
        if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) x += 1f;
        return x;
#else
        try { return Input.GetAxisRaw("Horizontal"); }
        catch (System.InvalidOperationException)
        {
            Debug.LogWarning("[PlayerController2D] No se pudo leer el teclado con el Input Manager antiguo.");
            allowKeyboardFallback = false;
            return 0f;
        }
#endif
    }

    private static bool FindBoolParameter(Animator anim, int nameHash)
    {
        if (anim == null || anim.runtimeAnimatorController == null) return false;
        foreach (var p in anim.parameters)
            if (p.type == AnimatorControllerParameterType.Bool && p.nameHash == nameHash)
                return true;
        return false;
    }
}
