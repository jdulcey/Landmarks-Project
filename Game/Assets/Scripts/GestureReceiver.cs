using System;
using UnityEngine;
#if !UNITY_WEBGL
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
#endif

/// <summary>
/// Entrega el gesto actual al juego: REPOSO (puño), PALMA, DERECHA, IZQUIERDA,
/// DOS_DEDOS, UN_DEDO o NINGUNO (sin mano / poco seguro).
///
/// Dos orígenes, misma API (CurrentGesture):
///   - Editor / Windows / etc.: UDP desde gesture_udp.py (hilo aparte), como antes.
///   - WebGL: el navegador (gestures.js) llama a OnGesture() con SendMessage.
///     En WebGL no existen los sockets UDP ni los hilos, por eso ese código se excluye.
///
/// Es un singleton persistente (DontDestroyOnLoad). Debe estar en un objeto RAÍZ de la
/// jerarquía y el GameObject debe llamarse EXACTAMENTE "GestureReceiver": SendMessage lo
/// busca por ese nombre. Si otra escena trae su propio GestureReceiver, el duplicado se
/// destruye solo.
/// Si no llega ningún mensaje durante timeoutSeconds, el gesto pasa a NINGUNO.
/// </summary>
public class GestureReceiver : MonoBehaviour
{
    public static GestureReceiver Instance { get; private set; }

#if !UNITY_WEBGL
    [SerializeField] private int port = 5005;
#endif
    [SerializeField] private float timeoutSeconds = 0.5f;

    /// <summary>Último gesto válido recibido (o NINGUNO si hay timeout). Leer desde Update.</summary>
    public string CurrentGesture { get; private set; } = "NINGUNO";

    private readonly object lockObj = new object();
    private string latest = "NINGUNO";
    private long lastReceiveTicks;

#if !UNITY_WEBGL
    private UdpClient client;
    private Thread thread;
    private volatile bool running;
#endif

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject); // ya existe uno persistente
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        lastReceiveTicks = DateTime.UtcNow.Ticks;

        // Sin esto Unity se pausa cuando la ventana de Python/cámara tiene el foco
        // y deja de procesar mensajes.
        Application.runInBackground = true;
    }

    /// <summary>
    /// Llamado desde JavaScript (WebGL): unityInstance.SendMessage("GestureReceiver", "OnGesture", gesto).
    /// Público y con un solo parámetro string, requisito de SendMessage.
    /// </summary>
    public void OnGesture(string gesture)
    {
        if (string.IsNullOrEmpty(gesture)) return;
        string msg = gesture.Trim().ToUpperInvariant();
        lock (lockObj)
        {
            latest = msg;
            lastReceiveTicks = DateTime.UtcNow.Ticks;
        }
    }

    private void Update()
    {
        if (Instance != this) return;

        string msg;
        long last;
        lock (lockObj)
        {
            msg = latest;
            last = lastReceiveTicks;
        }

        bool timedOut = (DateTime.UtcNow.Ticks - last) > TimeSpan.FromSeconds(timeoutSeconds).Ticks;
        CurrentGesture = timedOut ? "NINGUNO" : msg;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

#if !UNITY_WEBGL
    private void OnEnable()
    {
        if (Instance != this) return; // duplicado: no abrir el puerto

        lastReceiveTicks = DateTime.UtcNow.Ticks;
        running = true;
        try
        {
            client = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException e)
        {
            Debug.LogError($"[GestureReceiver] No se pudo abrir el puerto {port}: {e.Message}");
            running = false;
            return;
        }

        thread = new Thread(ReceiveLoop) { IsBackground = true };
        thread.Start();
        Debug.Log($"[GestureReceiver] Escuchando en udp://127.0.0.1:{port}");
    }

    private void ReceiveLoop()
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (running)
        {
            try
            {
                byte[] data = client.Receive(ref remote); // bloquea hasta recibir
                string msg = Encoding.UTF8.GetString(data).Trim().ToUpperInvariant();
                lock (lockObj)
                {
                    latest = msg;
                    lastReceiveTicks = DateTime.UtcNow.Ticks;
                }
            }
            catch (SocketException) { if (!running) break; }  // al cerrar el socket
            catch (ObjectDisposedException) { break; }
        }
    }

    private void OnDisable()
    {
        if (Instance != this) return;

        running = false;
        client?.Close();                 // desbloquea Receive() y libera el puerto
        if (thread != null && thread.IsAlive) thread.Join(500);
        client = null;
        thread = null;
    }
#endif
}