import socket

UDP_IP = "127.0.0.1"
UDP_PORT = 5005

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind((UDP_IP, UDP_PORT))
print(f"Escuchando en udp://{UDP_IP}:{UDP_PORT} ... (Ctrl + C para salir)")

last = None
try:
    while True:
        data, addr = sock.recvfrom(1024)
        msg = data.decode("utf-8").strip()
        if msg != last:
            print("Recibido:", msg)
            last = msg
except KeyboardInterrupt:
    pass
finally:
    sock.close()
