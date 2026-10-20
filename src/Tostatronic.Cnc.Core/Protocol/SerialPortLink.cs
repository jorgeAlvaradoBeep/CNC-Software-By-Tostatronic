using System.IO.Ports;

namespace Tostatronic.Cnc.Core.Protocol;

/// <summary>
/// Puerto serial real de la Wegstr: 9600 baudios, 8N1, sin control de flujo, DTR/RTS activos.
/// </summary>
public sealed class SerialPortLink : ISerialLink
{
    private readonly SerialPort _port;

    public SerialPortLink(string portName)
    {
        _port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            WriteTimeout = 1000,
        };
        _port.DataReceived += OnDataReceived;
    }

    public string Name => _port.PortName;
    public bool IsOpen => _port.IsOpen;
    public event Action<byte[]>? DataReceived;

    public void Open() => _port.Open();

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
    }

    public void Write(byte[] data) => _port.Write(data, 0, data.Length);

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            int n = _port.BytesToRead;
            if (n <= 0) return;
            var buffer = new byte[n];
            int read = _port.Read(buffer, 0, n);
            if (read <= 0) return;
            if (read < n) Array.Resize(ref buffer, read);
            DataReceived?.Invoke(buffer);
        }
        catch (Exception)
        {
            // el puerto se cerró o se desconectó el USB mientras leíamos; lo reporta el flujo normal
        }
    }

    public void Dispose()
    {
        _port.DataReceived -= OnDataReceived;
        Close();
        _port.Dispose();
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames();
}
