using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Zona de la escena de exploración: al entrar el jugador, carga la escena de batalla.
/// Requiere un Collider2D con "Is Trigger" activado (se activa solo al añadir el script).
/// El jugador necesita el tag "Player" y un Rigidbody2D.
/// Ambas escenas deben estar en Build Settings.
/// </summary>
[RequireComponent(typeof(Collider2D))]
public class BattleTrigger : MonoBehaviour
{
    [SerializeField] private string battleScene = "Battle";
    [SerializeField] private string playerTag = "Player";

    private bool triggered;

    private void Reset()
    {
        GetComponent<Collider2D>().isTrigger = true;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (triggered || !other.CompareTag(playerTag)) return;
        triggered = true;
        SceneManager.LoadScene(battleScene);
    }
}
