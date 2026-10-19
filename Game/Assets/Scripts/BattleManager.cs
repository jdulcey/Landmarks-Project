using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Batalla por turnos con habilidades por secuencia de gestos.
///
/// Turno del jugador: hay turnTime segundos para completar la secuencia de CUALQUIERA de las
/// habilidades. Un gesto cuenta cuando se mantiene holdTime segundos. Si los gestos hechos no
/// coinciden con el inicio de ninguna habilidad, los cuadrados se ponen en rojo un momento y
/// vuelven a negro para intentarlo otra vez (sin perder el turno).
///
/// Gestos que envía Python: REPOSO (puño), PALMA, DERECHA, IZQUIERDA, DOS_DEDOS, UN_DEDO, NINGUNO.
/// Prueba sin cámara: mantén 1 (PALMA), 2 (PUÑO), 3 (DOS_DEDOS) o 4 (UN_DEDO).
/// </summary>
public class BattleManager : MonoBehaviour
{
    [Serializable]
    public class Skill
    {
        public string name = "Golpe";
        [Tooltip("Gestos en orden. Valores: PALMA, REPOSO (= puño), DERECHA, IZQUIERDA, DOS_DEDOS, UN_DEDO")]
        public string[] sequence = { "PALMA", "REPOSO", "PALMA" };
        public int damage = 20;
        [Tooltip("Marcado = ataque a distancia: no se acerca, lanza un proyectil durante la animación Punch.")]
        public bool isProjectile = false;
    }

    private enum State { PlayerTurn, Acting, EnemyTurn, Finished }

    [Header("Habilidades")]
    [SerializeField] private Skill[] skills = DefaultSkills();

    [Header("Reglas")]
    [SerializeField] private float turnTime = 15f;
    [SerializeField] private float holdTime = 0.25f;
    [Tooltip("Segundos que los cuadrados quedan en rojo tras una secuencia equivocada.")]
    [SerializeField] private float errorDuration = 0.6f;
    [SerializeField] private int playerMaxHp = 100;
    [SerializeField] private int enemyMaxHp = 60;
    [SerializeField] private int enemyDamage = 15;

    [Header("Personajes")]
    [SerializeField] private Transform playerTransform;
    [SerializeField] private Transform enemyTransform;
    [SerializeField] private float attackStopDistance = 1.2f;
    [SerializeField] private float approachTime = 0.4f;

    [Header("Animaciones del jugador (nombres de ESTADOS del Animator)")]
    [Tooltip("Si se deja vacío se busca en el jugador o en sus hijos.")]
    [SerializeField] private Animator playerAnimator;
    [SerializeField] private string idleState = "Idle";
    [SerializeField] private string dashState = "Dash";
    [SerializeField] private string punchState = "Punch";
    [SerializeField] private string projectilState = "Punch2";
    [Tooltip("Momento del golpe dentro de la animación Punch (0 = inicio, 1 = final) en que se aplica el daño.")]
    [Range(0f, 1f)] [SerializeField] private float hitMoment = 0.5f;
    [Tooltip("Marca esto si tus sprites originales miran hacia la derecha.")]
    [SerializeField] private bool spriteFacesRight = true;

    [Header("Ataque a distancia (Lanzar objeto)")]
    [Tooltip("Cuántos frames (sprites) tiene tu animación Punch.")]
    [Min(1)] [SerializeField] private int punchTotalFrames = 4;
    [Tooltip("En qué frame de Punch sale el proyectil (1 = primer frame).")]
    [Min(1)] [SerializeField] private int projectileFrame = 3;
    [Tooltip("Opcional. Si se deja vacío se crea un círculo por código.")]
    [SerializeField] private GameObject projectilePrefab;
    [SerializeField] private Color projectileColor = new Color(1f, 0.85f, 0.2f);
    [SerializeField] private float projectileSize = 0.4f;
    [SerializeField] private float projectileSpeed = 12f;
    [Tooltip("Dónde aparece el proyectil respecto al centro del jugador (X se invierte según hacia dónde mira).")]
    [SerializeField] private Vector2 projectileSpawnOffset = new Vector2(0.8f, 0f);

    [Header("Efecto al recibir golpe (enemigo)")]
    [Tooltip("Si se deja vacío se busca en el enemigo o en sus hijos.")]
    [SerializeField] private SpriteRenderer enemySprite;
    [SerializeField] private Color hitColor = Color.red;
    [SerializeField] private float hitFlashDuration = 0.3f;

    [Header("Interfaz (TextMeshPro, todos opcionales)")]
    [SerializeField] private TMP_Text sequenceText;
    [Tooltip("Cuadrados de la secuencia (negro = pendiente, verde = hecho, rojo = error). Si lo usas, deja vacío 'Sequence Text'.")]
    [SerializeField] private SequenceDisplay sequenceDisplay;
    [Tooltip("Lista de habilidades disponibles con sus gestos (ayuda para el jugador).")]
    [SerializeField] private TMP_Text skillsHintText;
    [SerializeField] private TMP_Text detectedText;
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private TMP_Text playerHpText;
    [SerializeField] private TMP_Text enemyHpText;

    [Header("Barras (Slider, opcionales)")]
    [SerializeField] private Slider playerHpBar;
    [SerializeField] private Slider enemyHpBar;
    [SerializeField] private Slider timerBar;

    [Header("Fin de la batalla")]
    [Tooltip("Escena a la que volver al terminar (debe estar en Build Settings). Vacío = no volver.")]
    [SerializeField] private string returnScene = "";
    [SerializeField] private bool allowKeyboardDebug = true;

    private const string TurnMessage = "¡Tu turno! Haz la secuencia";

    private State state;
    private Color enemyOriginalColor = Color.white;
    private Coroutine flashRoutine;
    private float timer;
    private int playerHp, enemyHp;

    // Estado del reconocedor de secuencia
    private readonly List<string> input = new List<string>(); // gestos válidos hechos en este intento
    private float errorTimer;
    private string heldGesture = "NINGUNO";
    private float heldTimer;
    private string lastRegistered = "NINGUNO";

    private static Sprite circleSprite;

    private static Skill[] DefaultSkills()
    {
        return new Skill[]
        {
            new Skill(),
            new Skill
            {
                name = "Lanzar objeto",
                sequence = new[] { "DOS_DEDOS", "REPOSO", "UN_DEDO" },
                damage = 20,
                isProjectile = true
            }
        };
    }

    /// <summary>
    /// La lista guardada en la escena no recibe los valores nuevos del código, así que si falta
    /// una habilidad por defecto (mismo nombre o misma secuencia) se añade al iniciar.
    /// </summary>
    private void EnsureDefaultSkills()
    {
        var list = new List<Skill>(skills);
        foreach (Skill def in DefaultSkills())
        {
            bool exists = false;
            foreach (Skill s in list)
            {
                if (s == null) continue;
                if (s.name == def.name || SameSequence(s.sequence, def.sequence)) { exists = true; break; }
            }
            if (!exists)
            {
                list.Add(def);
                Debug.Log($"[BattleManager] Habilidad '{def.name}' no estaba en el Inspector; se añadió automáticamente.");
            }
        }
        skills = list.ToArray();
    }

    private static bool SameSequence(string[] a, string[] b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    [ContextMenu("Restaurar habilidades por defecto")]
    private void RestoreDefaultSkills()
    {
        skills = DefaultSkills();
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    private void Start()
    {
        if (skills == null || skills.Length == 0)
        {
            Debug.LogWarning("[BattleManager] La lista de habilidades estaba vacía; se usan las habilidades por defecto.");
            skills = DefaultSkills();
        }
        EnsureDefaultSkills();
        if (sequenceText == null && sequenceDisplay == null)
            Debug.LogWarning("[BattleManager] No hay 'Sequence Text' ni 'Sequence Display': la secuencia no se mostrará en pantalla.");
        if (playerTransform == null || enemyTransform == null)
            Debug.LogWarning("[BattleManager] Falta asignar 'Player Transform' y/o 'Enemy Transform': el ataque dará error al completar la secuencia.");

        if (playerAnimator == null && playerTransform != null)
            playerAnimator = playerTransform.GetComponentInChildren<Animator>();
        PlayState(idleState);

        if (enemySprite == null && enemyTransform != null)
            enemySprite = enemyTransform.GetComponentInChildren<SpriteRenderer>();
        if (enemySprite != null)
            enemyOriginalColor = enemySprite.color;

        playerHp = playerMaxHp;
        enemyHp = enemyMaxHp;
        RefreshHp();
        StartPlayerTurn();
    }

    // ------------------------------------------------------------------ Turnos

    private void StartPlayerTurn()
    {
        state = State.PlayerTurn;
        timer = turnTime;
        SetBar(timerBar, 1f);
        input.Clear();
        errorTimer = 0f;

        // Si la mano ya está en un gesto al empezar el turno, no cuenta hasta que lo cambie.
        string g = ReadGesture();
        heldGesture = g;
        heldTimer = 0f;
        lastRegistered = g;

        SetText(statusText, TurnMessage);
        SetText(skillsHintText, BuildSkillsHint());
        if (sequenceDisplay != null) sequenceDisplay.ShowEmpty(MaxSequenceLength());
        RefreshSequenceUI();
    }

    private void Update()
    {
        if (state != State.PlayerTurn) return;

        timer -= Time.deltaTime;
        string g = ReadGesture();

        if (errorTimer > 0f)
        {
            // Mostrando el error en rojo: se ignoran los gestos hasta que termine.
            errorTimer -= Time.deltaTime;
            if (errorTimer <= 0f) ResetAttempt(g);
        }
        else
        {
            ProcessGesture(g);
        }

        SetText(detectedText, g == "NINGUNO" ? "Detectado: -" : $"Detectado: {Display(g)}");
        SetText(timerText, Mathf.Max(0, Mathf.CeilToInt(timer)).ToString());
        SetBar(timerBar, timer / turnTime);

        if (state != State.PlayerTurn) return; // la secuencia se completó en este frame
        if (timer <= 0f) StartCoroutine(TurnTimeout());
    }

    // ------------------------------------------------------ Reconocer secuencia

    private void ProcessGesture(string g)
    {
        if (g == heldGesture) heldTimer += Time.deltaTime;
        else { heldGesture = g; heldTimer = 0f; }

        // Sin mano / poco seguro: permite volver a mostrar el mismo gesto como uno nuevo.
        if (heldGesture == "NINGUNO") { lastRegistered = "NINGUNO"; return; }

        if (heldTimer >= holdTime && heldGesture != lastRegistered)
        {
            lastRegistered = heldGesture;
            RegisterGesture(heldGesture);
        }
    }

    private void RegisterGesture(string g)
    {
        input.Add(g);

        // Se acepta cualquier gesto; solo se valida cuando la secuencia está completa.
        Skill completed = null;
        string[] attempt = input.ToArray();
        foreach (Skill s in skills)
        {
            if (s != null && SameSequence(s.sequence, attempt)) { completed = s; break; }
        }

        if (sequenceDisplay != null) sequenceDisplay.SetSlot(input.Count - 1, g, Display(g));
        RefreshSequenceUI();

        if (completed != null)
        {
            StartCoroutine(completed.isProjectile ? ProjectileAction(completed) : PlayerAction(completed));
        }
        else if (input.Count >= MaxSequenceLength())
        {
            // Se hicieron todos los gestos y no coinciden con ninguna habilidad: rojo y reinicio.
            if (sequenceDisplay != null) sequenceDisplay.ShowError();
            SetText(statusText, "¡Secuencia incorrecta!");
            errorTimer = errorDuration;
        }
    }

    private void ResetAttempt(string currentGesture)
    {
        input.Clear();
        // El gesto que se esté mostrando ahora no cuenta hasta que lo cambie.
        heldGesture = currentGesture;
        heldTimer = 0f;
        lastRegistered = currentGesture;

        if (sequenceDisplay != null) sequenceDisplay.ResetSlots();
        SetText(statusText, TurnMessage);
        RefreshSequenceUI();
    }

    // --------------------------------------------------------------- Acciones

    /// <summary>Ataque cuerpo a cuerpo: dash, golpe, regreso.</summary>
    private IEnumerator PlayerAction(Skill skill)
    {
        state = State.Acting;
        SetText(statusText, $"¡{skill.name}!");

        Vector3 start = playerTransform.position;
        Vector3 enemyPos = enemyTransform.position;
        // Solo se mueve en horizontal: mantiene su altura (Y) y se detiene a attackStopDistance del enemigo.
        float sideOfEnemy = Mathf.Sign(start.x - enemyPos.x);
        Vector3 target = new Vector3(enemyPos.x + sideOfEnemy * attackStopDistance, start.y, start.z);

        SpriteRenderer sr = playerTransform.GetComponentInChildren<SpriteRenderer>();
        bool originalFlip = sr != null && sr.flipX;
        float dirToEnemy = Mathf.Sign(enemyPos.x - start.x);

        // 1) Dash hacia el enemigo
        FaceDirection(sr, dirToEnemy);
        PlayState(dashState);
        yield return MoveTo(playerTransform, start, target, approachTime);

        // 2) Golpe: el daño se aplica a mitad de la animación (hitMoment)
        PlayState(punchState);
        yield return null; // un frame para poder leer la duración real del clip
        float clipLength = GetCurrentStateLength(0.4f);
        yield return new WaitForSeconds(clipLength * hitMoment);

        ApplyDamageToEnemy(skill.damage);
        yield return new WaitForSeconds(clipLength * (1f - hitMoment) + 0.15f);

        // 3) Regreso: dash mirando hacia el lado contrario
        FaceDirection(sr, -dirToEnemy);
        PlayState(dashState);
        yield return MoveTo(playerTransform, target, start, approachTime);

        // 4) De vuelta en su sitio
        if (sr != null) sr.flipX = originalFlip;
        PlayState(idleState);

        if (enemyHp <= 0) Finish(true);
        else StartCoroutine(EnemyTurn());
    }

    /// <summary>Ataque a distancia: anima Punch y en el frame indicado sale un proyectil.</summary>
    private IEnumerator ProjectileAction(Skill skill)
    {
        state = State.Acting;
        SetText(statusText, $"¡{skill.name}!");

        Vector3 enemyPos = enemyTransform.position;
        SpriteRenderer sr = playerTransform.GetComponentInChildren<SpriteRenderer>();
        bool originalFlip = sr != null && sr.flipX;
        float dirToEnemy = Mathf.Sign(enemyPos.x - playerTransform.position.x);

        FaceDirection(sr, dirToEnemy);
        PlayState(projectilState);
        yield return null; // un frame para poder leer la duración real del clip
        float clipLength = GetCurrentStateLength(0.4f);

        // El proyectil sale cuando empieza el frame "projectileFrame" de la animación.
        float spawnTime = clipLength * Mathf.Clamp01((projectileFrame - 1f) / Mathf.Max(1, punchTotalFrames));
        yield return new WaitForSeconds(spawnTime);

        bool hit = false;
        GameObject proj = SpawnProjectile(sr, dirToEnemy);
        StartCoroutine(FlyProjectile(proj, skill.damage, () => hit = true));

        // Termina la animación mientras el proyectil vuela.
        yield return new WaitForSeconds(Mathf.Max(0f, clipLength - spawnTime));
        PlayState(idleState);

        while (!hit) yield return null; // espera el impacto
        yield return new WaitForSeconds(0.4f);

        if (sr != null) sr.flipX = originalFlip;

        if (enemyHp <= 0) Finish(true);
        else StartCoroutine(EnemyTurn());
    }

    private GameObject SpawnProjectile(SpriteRenderer playerSr, float dirToEnemy)
    {
        float baseY = playerSr != null ? playerSr.bounds.center.y : playerTransform.position.y;
        Vector3 pos = new Vector3(
            playerTransform.position.x + dirToEnemy * projectileSpawnOffset.x,
            baseY + projectileSpawnOffset.y,
            playerTransform.position.z);

        if (projectilePrefab != null)
            return Instantiate(projectilePrefab, pos, Quaternion.identity);

        var go = new GameObject("Projectile");
        go.transform.position = pos;
        go.transform.localScale = Vector3.one * projectileSize;
        var psr = go.AddComponent<SpriteRenderer>();
        psr.sprite = GetCircleSprite();
        psr.color = projectileColor;
        if (playerSr != null)
        {
            psr.sortingLayerID = playerSr.sortingLayerID;
            psr.sortingOrder = playerSr.sortingOrder + 1;
        }
        else psr.sortingOrder = 10;
        return go;
    }

    private IEnumerator FlyProjectile(GameObject proj, int damage, Action onHit)
    {
        Vector3 target = GetEnemyHitPoint();
        while (proj != null && Vector3.Distance(proj.transform.position, target) > 0.05f)
        {
            proj.transform.position = Vector3.MoveTowards(proj.transform.position, target, projectileSpeed * Time.deltaTime);
            yield return null;
        }
        if (proj != null) Destroy(proj);

        ApplyDamageToEnemy(damage);
        onHit?.Invoke();
    }

    private Vector3 GetEnemyHitPoint()
    {
        if (enemySprite != null) return enemySprite.bounds.center;
        return enemyTransform.position;
    }

    private static Sprite GetCircleSprite()
    {
        if (circleSprite != null) return circleSprite;

        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float r = size / 2f;
        var center = new Vector2(r, r);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d))); // borde suave
            }
        tex.Apply();
        circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size); // 1 unidad
        return circleSprite;
    }

    private void ApplyDamageToEnemy(int damage)
    {
        enemyHp = Mathf.Max(0, enemyHp - damage);
        RefreshHp();
        FlashEnemy();
    }

    private IEnumerator TurnTimeout()
    {
        state = State.Acting;
        SetText(statusText, "¡Se acabó el tiempo!");
        yield return new WaitForSeconds(1f);
        StartCoroutine(EnemyTurn());
    }

    private IEnumerator EnemyTurn()
    {
        state = State.EnemyTurn;
        if (sequenceDisplay != null) sequenceDisplay.Hide();
        SetText(statusText, "Turno del enemigo...");
        SetText(sequenceText, "");
        SetText(skillsHintText, "");
        SetText(detectedText, "");
        yield return new WaitForSeconds(1f);

        playerHp = Mathf.Max(0, playerHp - enemyDamage);
        RefreshHp();
        SetText(statusText, $"El enemigo te hace {enemyDamage} de daño");
        yield return new WaitForSeconds(1f);

        if (playerHp <= 0) Finish(false);
        else StartPlayerTurn();
    }

    private void Finish(bool victory)
    {
        state = State.Finished;
        if (sequenceDisplay != null) sequenceDisplay.Hide();
        SetText(statusText, victory ? "¡Victoria!" : "Derrota...");
        SetText(sequenceText, "");
        SetText(skillsHintText, "");
        SetText(detectedText, "");
        if (!string.IsNullOrEmpty(returnScene)) StartCoroutine(ReturnAfterDelay(3f));
    }

    private IEnumerator ReturnAfterDelay(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        SceneManager.LoadScene(returnScene);
    }

    private void FlashEnemy()
    {
        if (enemySprite == null) return;
        if (flashRoutine != null) StopCoroutine(flashRoutine);
        flashRoutine = StartCoroutine(HitFlash());
    }

    private IEnumerator HitFlash()
    {
        enemySprite.color = hitColor;
        yield return new WaitForSeconds(hitFlashDuration);
        enemySprite.color = enemyOriginalColor;
        flashRoutine = null;
    }

    private void PlayState(string stateName)
    {
        if (playerAnimator != null && !string.IsNullOrEmpty(stateName))
            playerAnimator.Play(stateName, 0, 0f);
    }

    private float GetCurrentStateLength(float fallback)
    {
        if (playerAnimator == null) return fallback;
        float len = playerAnimator.GetCurrentAnimatorStateInfo(0).length;
        return len > 0.05f ? len : fallback;
    }

    private void FaceDirection(SpriteRenderer sr, float dirX)
    {
        if (sr == null) return;
        sr.flipX = spriteFacesRight ? dirX < 0f : dirX > 0f;
    }

    private static IEnumerator MoveTo(Transform t, Vector3 from, Vector3 to, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            t.position = Vector3.Lerp(from, to, Mathf.SmoothStep(0f, 1f, elapsed / duration));
            yield return null;
        }
        t.position = to;
    }

    // ------------------------------------------------------------------ Entrada

    private string ReadGesture()
    {
        if (allowKeyboardDebug)
        {
            string k = ReadDebugKey();
            if (k != null) return k;
        }
        var receiver = GestureReceiver.Instance;
        return receiver != null ? receiver.CurrentGesture : "NINGUNO";
    }

    private static string ReadDebugKey()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return null;
        if (kb.digit1Key.isPressed) return "PALMA";
        if (kb.digit2Key.isPressed) return "REPOSO";
        if (kb.digit3Key.isPressed) return "DOS_DEDOS";
        if (kb.digit4Key.isPressed) return "UN_DEDO";
        return null;
#else
        if (Input.GetKey(KeyCode.Alpha1)) return "PALMA";
        if (Input.GetKey(KeyCode.Alpha2)) return "REPOSO";
        if (Input.GetKey(KeyCode.Alpha3)) return "DOS_DEDOS";
        if (Input.GetKey(KeyCode.Alpha4)) return "UN_DEDO";
        return null;
#endif
    }

    // ---------------------------------------------------------------- Interfaz

    private int MaxSequenceLength()
    {
        int max = 1;
        foreach (Skill s in skills)
            if (s != null && s.sequence != null && s.sequence.Length > max) max = s.sequence.Length;
        return max;
    }

    /// <summary>Texto "PALMA → ? → ?" con lo hecho hasta ahora en verde.</summary>
    private void RefreshSequenceUI()
    {
        if (sequenceText == null) return;
        int n = MaxSequenceLength();
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append(i < input.Count ? $"<color=#4CAF50>{Display(input[i])}</color>" : "?");
            if (i < n - 1) sb.Append("  →  ");
        }
        SetText(sequenceText, sb.ToString());
    }

    private string BuildSkillsHint()
    {
        var sb = new StringBuilder();
        for (int s = 0; s < skills.Length; s++)
        {
            Skill skill = skills[s];
            if (skill == null || skill.sequence == null) continue;
            sb.Append(skill.name).Append(": ");
            for (int i = 0; i < skill.sequence.Length; i++)
            {
                sb.Append(Display(skill.sequence[i]));
                if (i < skill.sequence.Length - 1) sb.Append(" → ");
            }
            if (s < skills.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void RefreshHp()
    {
        SetText(playerHpText, $"Jugador: {playerHp}/{playerMaxHp}");
        SetText(enemyHpText, $"Enemigo: {enemyHp}/{enemyMaxHp}");
        SetBar(playerHpBar, (float)playerHp / playerMaxHp);
        SetBar(enemyHpBar, (float)enemyHp / enemyMaxHp);
    }

    private static string Display(string g)
    {
        switch (g)
        {
            case "REPOSO": return "PUÑO";
            case "DERECHA": return "SEÑALAR DER.";
            case "IZQUIERDA": return "SEÑALAR IZQ.";
            case "DOS_DEDOS": return "2 DEDOS";
            case "UN_DEDO": return "1 DEDO";
            default: return g;
        }
    }

    private static void SetBar(Slider bar, float normalized)
    {
        if (bar != null) bar.value = Mathf.Clamp01(normalized);
    }

    private static void SetText(TMP_Text t, string s)
    {
        if (t != null) t.text = s;
    }
}
