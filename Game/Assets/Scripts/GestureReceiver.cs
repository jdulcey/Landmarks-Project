using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Recibe por UDP (texto plano) el gesto enviado por Python:
/// REPOSO (puño), PALMA, DERECHA, IZQUIERDA o NINGUNO (sin mano / poco seguro).
///
/// Es un singleton persistente: sobrevive al cambio de escena (DontDestroyOnLoad) para
/// mantener un solo socket abierto. Debe estar en un objeto RAÍZ de la jerarquía (sin padre).
/// Si otra escena trae su propio GestureReceiver, el duplicado se destruye solo.
///
/// La recepción es bloqueante, así que corre en un hilo aparte; el hilo solo guarda el
/// último mensaje y Unity lo lee desde el hilo principal (la API de Unity no es thread-safe).
/// Si no llega ningún paquete durante timeoutSeconds, el gesto pasa a NINGUNO.
/// </summary>
public class GestureReceiver : MonoBehaviour
{
    public static GestureReceiver Instance { get; private set; }

    [SerializeField] private int port = 5005;
    [SerializeField] private float timeoutSeconds = 0.5f;

    /// <summary>Último gesto válido recibido (o NINGUNO si hay timeout). Leer desde Update.</summary>
    public string CurrentGesture { get; private set; } = "NINGUNO";

    private UdpClient client;
    private Thread thread;
    private volatile bool running;

    private readonly object lockObj = new object();
    private string latest = "NINGUNO";
    private long lastReceiveTicks;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject); // ya existe uno persistente
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // Sin esto Unity se pausa cuando la ventana de Python/cámara tiene el foco
        // y deja de procesar mensajes.
        Application.runInBackground = true;
    }

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

    private void OnDisable()
    {
        if (Instance != this) return;

        running = false;
        client?.Close();                 // desbloquea Receive() y libera el puerto
        if (thread != null && thread.IsAlive) thread.Join(500);
        client = null;
        thread = null;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
